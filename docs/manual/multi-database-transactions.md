# Multi-database transactions

A multi-database transaction changes the Root database and any number of Child databases
as **one** transaction: either every database keeps its changes, or none does. It holds
even across a crash at any point. Nobody else can see a half-finished transaction.

```csharp
var result = await ctx.PlanMultiTx()
    .Add(sessionId, static (db, tx) => { tx.Match.Update(...); return Result.Ok(); })   // a Child
    .Add(static (db, tx) => { tx.Wallet.Update(...); return Result.Ok(); })             // the Root
    .Commit();
```

This page covers when to use one, the two kinds, how to write the bodies, what can go
wrong, and what the engine does underneath: commit, WAL, crash recovery.

---

## 1. Do you need one?

Usually not. **A plain `ctx.BeginTx(...)` on one database is the right tool most of the
time.** It's already atomic and isolated, and it's the cheapest thing the engine does.

Reach for a multi-database transaction only when a single business action must change
**more than one database** and a half-done result would be wrong:

| Situation | Use |
|---|---|
| Change rows in one database | `BeginTx` |
| Change the Root and a Child (or two Children) together, all-or-nothing | `PlanMultiTx` |
| Same, but a value from the transaction has to drive code **outside** the database (an HTTP call, a file, a lookup) before the rest of the transaction can run | `LockMultiTx` (rarely) |
| Move a result from a Child to the Root *after* the Child is finished, where a moment of delay is fine | Two `BeginTx` calls, the second one idempotent (see §8) |

Typical uses in a game server:

- **Spend and grant.** Take coins from the player's Root wallet and grant an item in their
  session Child. Neither may happen without the other.
- **Settle a match.** Read the final score in the match Child, mark it settled, and credit
  the reward to the Root, all at once.
- **Move between Children.** Take a player out of one lobby Child and seat them in
  another.

## 2. The two kinds

Both kinds give the same guarantee and share the same commit, durability and recovery.
They differ in **when** the databases are locked and **when your code runs**.

### Planned: `ctx.PlanMultiTx()` → `PlannedMultiTx`

You describe every step up front. Nothing runs until `Commit`. `Commit` locks every
database the steps touch, runs the steps in the order you added them, and commits.

```csharp
var multi = ctx.PlanMultiTx();
var score = multi.Add(sessionId, static (db, tx, matchId) => {
    var match = tx.Match.Primary.Find(matchId);
    if (!match.HasRow()) return Result<int>.Error(DbError.IndexKeyNotFound());
    tx.Match.Update(matchId, match.Get().Unwrap() with { Settled = true });
    return Result.Ok(match.Get().Unwrap().Score);
}, matchId);

multi.Add(static (db, tx, s) => {
    tx.Wallet.Update(s.PlayerId, ...credit s.Score.Value...);
    return Result.Ok();
}, (PlayerId: playerId, Score: score));

Result committed = await multi.Commit();
```

- Databases are locked **only for the length of `Commit`**, for as long as the steps
  take to run. This is the default choice.
- Steps can't run your own code between them. They are pure database bodies.
- A step can pass a value to a later step with `TxValue<T>` (§4), in either direction:
  Child to Root, Root to Child, Child to Child.

### Locked: `ctx.LockMultiTx(...)` → `LockedMultiTx`

You name the databases first. They are locked **immediately and stay locked** until you
`Commit`, `Rollback`, or dispose. Each `Run` executes right away and hands its value
straight back to your code, so you can do anything between steps, including I/O.

```csharp
await using var tx = (await ctx.LockMultiTx(p => p.RootDb().SessionDb(sessionId))).Unwrap();

var score = (await tx.Run(sessionId, static (db, t, id) => Result.Ok(ScoreOf(t, id)), matchId)).Unwrap();
var bonus = await rewardsService.LookupBonus(score);          // any code, while both databases stay locked

var credited = await tx.Run(static (db, t, a) => { t.Wallet.Update(...a.Bonus...); return Result.Ok(); },
    (PlayerId: playerId, Bonus: bonus));
if (credited.IsError()) return credited;                        // already aborted and unlocked

return await tx.Commit();
```

- `p.RootDb()` and `p.SessionDb(key)` are generated, one method per database, named
  after the database class.
- **Every database you'll touch must be declared up front.** A `Run` on an undeclared one
  fails with `MultiTxParticipantNotDeclared` and aborts the whole transaction.
- **The cost is the lock itself.** While it's open, *nothing else* can read or write those
  databases. Every other request queues behind you. Use it rarely and keep it short
  (§7).

### Choosing

Start with **Planned**. Switch to **Locked** only when a value from inside the
transaction must leave the database (to your code, a service, a file) *and* the
transaction must stay isolated while that happens. If isolation during the outside call
isn't actually required, split the work into separate `BeginTx` calls instead. That
never locks anything for longer than one body.

## 3. Writing the steps

### The short forms

Generated extension methods infer the database from the lambda, so you rarely write a
type argument:

| Form | Meaning |
|---|---|
| `Add(static (db, tx) => ...)` | a Root step |
| `Add(static (db, tx, args) => ..., args)` | a Root step with arguments |
| `Add(key, static (db, tx) => ...)` | a Child step (`key` picks the Child) |
| `Add(key, static (db, tx, args) => ..., args)` | a Child step with arguments |

`Run` on a `LockedMultiTx` has the same four shapes. A body returning `Result` gives you
back the transaction (Planned, so you can chain) or a `Result` (Locked). A body returning
`Result<T>` gives you a `TxValue<T>` (Planned) or a `Result<T>` (Locked).

**One known ambiguity:** two Child database types with the **same key type**, and a
body that compiles for both (for example, it touches no table). Then the compiler can't
pick one (`CS0121`). Annotate the lambda parameters
(`static (SessionDb db, SessionDbTransaction tx) => ...`). A body that touches a table only
one of them has resolves on its own.

### Rules for bodies

- **Write them `static` and pass data through `args`.** Bodies run later and on the
  database's own thread, so a captured variable is a hidden allocation and an easy bug.
  `static` makes the compiler enforce capture-free bodies. Pass a tuple when you need
  several values.
- **Only touch the database you were given.** A body for the Root works with the Root's
  `tx` only. It must never start another transaction or await anything.
- **Return a `Result`, don't throw.** Returning an error aborts the whole transaction.
  An exception thrown by a body is caught and treated the same way, but an error kind
  tells the caller far more.
- **Bodies run in the order you add them**, across databases. Within a database, a
  later step sees an earlier step's writes (insert in one step, find the row in the
  next). Nobody outside the transaction sees them until it commits.

## 4. Values: `TxValue<T>` and `Run` results

### Planned: `TxValue<T>`

`Add` with a value-returning body hands back a `TxValue<T>`, a placeholder that the
body fills when it runs inside `Commit`.

- **Inside a later step**, read it with `.Value`. It's already produced, because
  steps run in `Add` order.
- **Reading it in a step that runs *before* its producer** fails that step with
  `MultiTxValueNotProduced`, and the transaction aborts. Make sure the producer was added first.
- **After `Commit` succeeds**, `.Value` is yours to keep.
- **If the transaction failed or was rolled back**, `.Value` throws: its body's writes
  were undone, so the value describes nothing real. Check `HasValue` if unsure.

### Locked: values come back immediately, so treat them as provisional

`Run` returns its value as a plain `Result<T>` **before** `Commit`. That's the point of
the Locked kind, but it means the value belongs to a transaction that hasn't committed
yet. If a later step fails, or you roll back, or `Commit` fails, every write that
produced the value is undone.

**Don't act on a Locked value outside the database in a way you can't take back until
`Commit` has returned `Ok`.** Use it to decide, compute, or prepare. Send the email,
charge the card, or tell the client only after the commit. If an outside effect must
happen in the middle, it needs its own way to be undone or retried. The database can't
undo it for you.

## 5. Ending a transaction

| Call | Planned | Locked |
|---|---|---|
| `Commit(mode = Optimistic)` | runs the steps, then commits. Returns `Result` | commits what the `Run`s did. Returns `Result` |
| `Rollback()` | discards the plan (nothing had run) | undoes every `Run` and unlocks |
| `DisposeAsync` / `await using` | n/a | same as `Rollback` if still open; does nothing after `Commit` |

- **Single use.** After `Commit` or `Rollback`, further `Commit`/`Run` calls return
  `MultiTxAlreadyFinished`. Calling `Add` or `Rollback` on a finished Planned transaction
  throws, because those don't return a `Result`.
- **A failed `Run` ends a Locked transaction on the spot.** Everything is undone and the
  databases are unlocked right away, not when you get around to disposing. Later
  `Run`/`Commit` calls return that same failure.
- **`Rollback` can't undo a committed transaction.** Once `Commit` returns `Ok`, the only
  way back is a new transaction that writes the reverse.
- **Always use `await using` for a Locked transaction**, so an exception in your own code
  between steps can't leave the databases locked.

### The `mode` argument

`PropagationMode.Optimistic` (the default) and `Confirmed` mean what they mean for
`BeginTx`, but **only when at most one database has durable changes** (§6). When two or
more do, the commit always waits for the disk on every one of them. That's the price
of atomicity across files, and `mode` doesn't change it.

## 6. What happens at commit

### Locking

Databases are locked in **one fixed order, everywhere**: the Root first, then Children
sorted by their path. The order you add steps or declare databases doesn't matter.
Because every transaction locks in the same order, two multi-database transactions can
never deadlock each other. Your steps still *run* in the order you wrote them; only
the locking is reordered.

Locking a database means occupying its single writer loop. Every other request for that
database (`BeginTx`, queries, other transactions) waits in line until the transaction
ends. No thread is blocked while it waits; requests simply queue up.

### Applying

Each step's writes are applied to the in-memory tables as soon as the step runs, and
the engine keeps an undo record of everything it changed. If anything fails before the
commit point, the undo record restores every database exactly. If even the undo fails
(a bug or a corrupted state), that database is **poisoned**: it refuses all further
work instead of continuing with unknown data, and you'll see `ApplyFailed`.

### Making it durable

Which path runs depends on how many databases hold **durable changes**, meaning writes
to `Persistent` tables. `Instant` tables live in memory only and need no disk work.

**Zero or one durable database.** It commits exactly like a plain `BeginTx`: one WAL
entry holding every step's changes. That single entry is atomic by itself, so no extra
protocol is needed and it costs the same as an ordinary commit. `mode` applies here.

**Two or more durable databases: two-phase commit.**

1. **Prepare.** Every durable database writes a *prepare* record to its own WAL,
   holding all its changes and the list of participating databases, and waits until it
   is on disk. They do this in parallel.
2. **The commit point.** The moment **every** prepare is on disk, the transaction is
   committed, even if the process dies one instruction later. Recovery will finish it
   (§6, below).
3. **Markers.** Each database then appends a small *commit* marker and unlocks. Markers
   aren't waited on. They are a shortcut for recovery, not the decision itself.

If any prepare fails to reach the disk, the transaction aborts. The engine first writes
an **abort** decision to `chains.log` (below) and waits for it to reach the disk. That
matters: the failed prepare's bytes may still have reached the file, and without the
recorded abort a restart could find every prepare present and wrongly finish a
transaction your code was told had failed. If even that decision can't be written, the
outcome is genuinely unknown. The call returns `MultiTxOutcomeUnknown` and the involved
databases are poisoned rather than guessing.

### Files on disk

| File | What it holds |
|---|---|
| `{coldPath}/wal.dat` | the Root's WAL, including its prepares and markers |
| `{coldPath}/Children/{ChildType}/{key}/wal.dat` | each Child's own WAL |
| `{coldPath}/chains.log` | recorded decisions (commit/abort) for transactions that recovery or a failure had to settle, plus which databases have acknowledged them |

`chains.log` is small and checksummed per record. A decision stays in it only until
every participating database has absorbed its prepare. After that it is compacted away
on the next start.

## 7. When it's dangerous

### Locked transactions block everyone

While a `LockedMultiTx` is open, its databases serve **nobody else**. If you lock the Root
and then call a slow service, every request in your server that touches the Root waits for
that service. There's no timeout: the lock lasts until you end it.

- Lock as few databases as possible. **Avoid locking the Root** unless you truly need it.
- Do the slow work *before* `LockMultiTx` when you can, and pass the result in.
- Keep the time between `LockMultiTx` and `Commit` as short as the work allows.

### Waiting spreads

Fixed-order locking prevents deadlocks, but it doesn't stop one slow transaction from
holding up unrelated ones. Say a Locked transaction holds a Child while it waits on I/O,
and a Planned transaction needs the Root and that Child. The Planned one locks the Root
first (fixed order) and then waits for the Child, so **the Root is now blocked for everyone**,
even though the slow transaction never touched it.

### Never wait on your own lock

Inside an open Locked transaction, **never `await` a `BeginTx` (or another
multi-database transaction) on a database you're holding.** It queues behind your own
lock, which is only released after it completes: a permanent hang. Do that work through
`tx.Run(...)` instead.

### Provisional values

Covered in §4: a Locked `Run` value can still be undone. Don't let it escape into
something irreversible before `Commit` succeeds.

### Bodies that do too much

Planned bodies run while every participating database is locked. A body that loops over
a huge table, or does heavy computation, stretches that lock for everyone. Keep bodies to
the reads and writes the transaction needs.

### Throughput cost

With two or more durable databases, every commit waits for one disk flush per durable
database (in parallel), even under `Optimistic`. That's far more expensive than a plain
`BeginTx`. Don't put a multi-database transaction on a hot path that runs per frame or per
message if separate transactions would do.

### Hooks can't open them across Children

A `RhinoCtx` from a lifecycle hook isn't backed by the host, so it can't reach Child
databases. Adding a Child step there throws `ChildDatabaseRequiresHostException` (`Add`
is fluent, so it can't return an error). `BeginTx` and `LockMultiTx` return
`ChildDatabaseRequiresHost` instead.

## 8. Settling a finished Child into the Root

A common pattern: a session Child ends, and its result must be recorded in the Root.

- **If it must be atomic** (the Child marks the match settled *and* the Root credits the
  reward, or neither does), do it in **one Planned transaction**, the score flowing Child
  to Root through `TxValue<T>`. Then dispose the Child, then forget its key.
- **If a short delay is acceptable**, two plain `BeginTx` calls are cheaper. Write the
  Root side **idempotently** (keyed by match id, so running it twice credits once), then
  dispose the Child **last**. A crash in between then just repeats the idempotent write.

Disposing a Child that ever took part in a multi-database transaction is safe. Before
its directory is deleted, the engine records the outcome of each of its unresolved
prepares in `chains.log`. A sibling database that still needs that evidence after a
crash can find it there.

## 9. Crash recovery

Recovery runs automatically: for the Root when the server starts, and for each Child
when it's next activated (Children activate lazily, on first use).

For every prepare it finds in a database's WAL that hasn't been folded into the
database yet, recovery decides **commit or abort** in this order:

1. **A decision already in `chains.log`** wins.
2. **A local marker** (commit/abort) in this database's own WAL is used next.
3. Otherwise it **asks the other participants**: it reads their WAL files. If every one
   of them has the prepare (or a commit marker), the transaction had reached its commit
   point, so **it is completed, not reverted**. If any one is missing it, or has an abort
   marker, it never committed and is dropped.

The decision is then written to `chains.log` (and flushed), so every other participant
reaches the same answer however late it recovers. The prepare is applied or skipped,
and the database acknowledges it.

What that means for your data, by when the crash happened:

| Crash happened... | After restart |
|---|---|
| before or during the steps | nothing was written to any WAL; no trace |
| after some, but not all, prepares were on disk | **aborted** on every database |
| after every prepare was on disk, before the markers | **committed** on every database |
| after the markers | committed |
| single durable database, before its entry was on disk | nothing; as with a plain `BeginTx` |

The rule is to **complete what reached the commit point, and drop only what never did.**
Recovery never reverts a transaction that may already have been reported as committed.

**Replay mode** (rebuilding memory from the WAL history instead of libmdbx) applies the
same decision to prepares still in the live WAL. A local marker is enough there, and
otherwise it asks the other participants.

## 10. Error reference

| Error kind | Meaning |
|---|---|
| your own (e.g. `Custom`, `IndexKeyNotFound`) | a body returned it; everything was undone |
| `DuplicateKey` and other constraint kinds | a step's writes broke a constraint. In both kinds it fails the step that caused it, not `Commit` |
| `MultiTxValueNotProduced` | a step read a `TxValue<T>` whose producer runs later |
| `MultiTxParticipantNotDeclared` | Locked: `Run` on a database not declared in `LockMultiTx` |
| `MultiTxNoParticipantsDeclared` | Locked: `LockMultiTx` declared no databases |
| `MultiTxAlreadyFinished` | `Commit`/`Run` after the transaction already ended |
| `ChildDatabaseRequiresHost` | a Child was used from a hook's `RhinoCtx` |
| `WalDurabilityFailed` | a prepare couldn't reach the disk; the transaction was aborted |
| `MultiTxOutcomeUnknown` | the abort decision couldn't be recorded either; the databases involved are poisoned |
| `ApplyFailed` | applying or undoing failed internally; that database is poisoned |
| `DatabaseClosing` | a database was shutting down when the transaction tried to lock it |

## 11. Checklist

- [ ] Could this be one `BeginTx`, or two idempotent ones? Prefer that.
- [ ] Planned unless a value must leave the database mid-transaction.
- [ ] Bodies are `static`, take their data through `args`, touch only their own `tx`, return
      errors instead of throwing.
- [ ] Producers of `TxValue<T>` are added before their consumers.
- [ ] Locked: `await using`; fewest databases; Root only if necessary; no slow work you
      could do before locking; no `BeginTx` on a database you hold.
- [ ] Locked: nothing irreversible happens with a `Run` value until `Commit` returns `Ok`.
- [ ] Settling a Child: one Planned transaction (atomic), or idempotent Root write, then
      dispose the Child last.
