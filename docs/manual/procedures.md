# Procedures

A **procedure** is a server method a client can call. You write a static method and mark
it `[Procedure]`, and RhinoDB generates the rest:
- the code that decodes the client's arguments;
- the call into your method;
- the code that encodes the result;
- the registration with the host.

```csharp
public static class Shop {
    // Transaction-only: one atomic transaction, synchronous.
    [Procedure]
    public static Result BuyItem(RootDbTxCtx ctx, int itemId, int price) {
        var wallet = ctx.Tx.Wallet.Primary.Find(ctx.Identity.Principal.Value).Get();
        if (wallet.IsError()) return wallet.Void();
        if (wallet.Unwrap().Coins < price) return Result.Error(DbError.Custom(ShopErrors.NotEnoughCoins));
        // ... take the coins, grant the item
        return Result.Ok();
    }

    // General: async, may do I/O and any number of transactions.
    [Procedure]
    public static async Task<Result<Receipt>> Checkout(RhinoCtx ctx, int cartId, CancellationToken ct) {
        var price = await ctx.BeginTx(static (db, tx, id) => /* read the cart */, cartId);
        // ... call a payment provider, then write the outcome in another transaction
    }
}
```

```csharp
var host = await RhinoHostBuilder.Create()
    .AddGeneratedProcedures()          // every [Procedure] in the project
    .AddGeneratedChildDatabases()
    .AddDatabase<RootDb, RootDbTransaction>(o => o.CreateDb = cold => new RootDb(cold))
    .BuildAsync();
```

---

## 1. Two shapes, one attribute

The generator picks the shape from the **first parameter**:

| | Transaction-only | General |
|---|---|---|
| First parameter | `{Db}TxCtx ctx` (`RootDbTxCtx`, `MarketDbTxCtx`, ...) | `RhinoCtx ctx` |
| Returns | `Result`: no value (§1.1) | `Task<Result>`, `Task<Result<T>>`, `ValueTask<...>` |
| Runs as | exactly **one** transaction, synchronously | your code, with as many `BeginTx` / multi-database transactions as it needs |
| May do I/O, `await` | no | yes |
| Databases | the Root, or a **singleton** Child (`[ChildDatabase<TRoot>]`) | any, including keyed Children (`ctx.BeginTx(key, ...)`) |
| `CancellationToken` | no | optional, as the **last** parameter |

Clients see no difference and call both the same way.

**Prefer the transaction-only shape.** It is atomic, it can't block on I/O, and it is
deterministic given its inputs (see §4). That is what later features build on: safe retry,
client-side prediction, and replaying a captured session. RhinoDB records the shape as
`ProcedureDescriptor.SingleTransaction`.

Use the general shape when you need something one transaction can't give you:
- an `await` (an HTTP call, a file);
- a keyed Child, whose key you look up first;
- several transactions;
- `PlanMultiTx` / `LockMultiTx`.

### 1.1 Return values: data reaches clients through views

Clients get data from **views and subscriptions**, not from procedure replies. A procedure
changes the database, and every subscribed client, the caller included, sees the change in
its views. This is the same split as SpacetimeDB:

- **A transaction-only procedure returns `Result` only**, like a SpacetimeDB reducer.
  Returning `Result<T>` is error **RHINO047**. On success the caller gets the 7-byte reply
  header and nothing else.
- **A general procedure may return `Result<T>`**, like a SpacetimeDB procedure. The value
  goes **to the caller only** and is never broadcast. Use it for what isn't in a view: the
  outcome of a call to an outside service (a payment receipt, a verification result).

If the client needs to know what its call created, let the client choose the id and pass it
in, then watch for it in the view. For reads, use views; for request/response data outside
the realtime connection, use a REST command.

### Why keyed Children need the general shape

A keyed Child (`[ChildDatabase<RootDb, string>]`) needs its key, and the key usually
comes from somewhere else first: the Root, or a lookup. So there is no `SessionDbTxCtx`.
Writing one is error **RHINO044**. Look the key up, then open the transaction:

```csharp
[Procedure]
public static async Task<Result> Forfeit(RhinoCtx ctx, int matchId) {
    var key = await ctx.BeginTx(static (db, tx, id) => /* find the match's session key */, matchId);
    if (key.IsError()) return key.Void();
    return await ctx.BeginTx(key.Unwrap(), static (db, tx, id) => { /* ... */ return Result.Ok(); }, matchId);
}
```

---

## 2. The contexts

### `{Db}TxCtx`: transaction-only procedures

Generated for the Root and for every singleton Child. It holds only what one transaction
may use:

| Member | |
|---|---|
| `Tx` | That database's transaction. Tables are `ctx.Tx.Wallet` (Persistent) and `ctx.Tx.Instant.Lobby` (Instant). |
| `Session` | The caller's connection: `ConnectionId`, `Identity`, `AppVersion`. |
| `Identity` | Shortcut for `Session.Identity`. |
| `Timestamp` | UTC, taken once when the transaction starts on the database's loop. Use it instead of `DateTime.UtcNow`. |
| `ServerVersion` | `Server.Version` from `rdbsettings.json`, packed. |
| `Random` | A `RhinoRandom` seeded for this transaction (§4). |

There is no host, no `BeginTx` and no other database in it. It's a `ref struct`, so it
can't be stored in a field, captured by a lambda or kept across an `await`. The
transaction can't escape the method.

### `RhinoCtx`: general procedures (and lifecycle hooks)

`Session`, `Identity`, `ServerVersion`, `Random`, and the ways to open transactions:
`BeginTx(...)` (with or without a key, with or without args), `PlanMultiTx()`,
`LockMultiTx(...)`.

Pass request values into a transaction body with the **args forms**. They allocate no
closure and keep the body `static`:

```csharp
await ctx.BeginTx(static (db, tx, a) => { tx.Wallet.Insert(new Wallet(a.Id, a.Coins)); return Result.Ok(); },
                  (Id: walletId, Coins: 100));
```

---

## 3. Parameters and results

A parameter, or a general procedure's `Result<T>` value, may be:
- a primitive (`int`, `long`, `bool`, `double`, `decimal`, `char`, ...), an enum or a `string`;
- a `Guid`, `DateTime`, `DateTimeOffset` or `TimeSpan`;
- a one-dimensional array of any of the above;
- a `[CustomType]`.

Anything else is error **RHINO045**: `object`, `List<T>`, a nullable such as `int?`,
jagged arrays, plain classes.

Arguments travel in the project's `Generator.ClientProtocol` (see
[configuration](configuration.md)), one envelope per call:

| Protocol | Arguments on the wire | Old/new clients |
|---|---|---|
| `Raw` | the fields back to back, RhinoDB's own codec | must match exactly |
| `VersionedMemoryPack` | identical to a `[MemoryPackable(GenerateType.VersionTolerant)]` type with the parameters as members, in order | tolerated |
| `MessagePack` | identical to a `[MessagePackObject]` type with `[Key(0)]`, `[Key(1)]`, ... members, in order | tolerated |

"Tolerated" means:
- an older client that sends fewer arguments gets defaults for the missing ones;
- a newer client that sends more has the extras ignored.

So add parameters **only at the end**, and never reorder or remove them. Under
`VersionedMemoryPack` / `MessagePack`, a `[CustomType]` parameter also needs that format's
attribute (RHINO045 says which one).

A client never needs to hand-write any of this. For every procedure, the generated
`GeneratedProcedures.{Namespace}_{Class}_{Method}` class has `Name`, `Hash`,
`SingleTransaction`, `EncodeArgs(...)` and (for `Result<T>`) `DecodeResult(...)`. The
client SDK generator will build on those.

### Names

A procedure is routed by `NameHash` of its name, which is the method name unless you set
one: `[Procedure(Name = "market.list")]`. Two procedures with the same name, even in
different classes, are error **RHINO046**. Give one of them a `Name`.

---

## 4. Randomness

Use `ctx.Random`, never `System.Random` or `Random.Shared`:

```csharp
var roll = ctx.Random.Next(6);              // Result<int>, 0..5
if (ctx.Random.Chance(0.05).Unwrap()) ...   // a 5% drop
var item = ctx.Random.WeightedPick(weights);
```

- It is a `RhinoRandom` (xoshiro256**). The same seed gives the same draws on every OS and
  .NET version.
- Each transaction (in `{Db}TxCtx`) and each request (in `RhinoCtx`) gets its own seed,
  generated unpredictably on first use. A procedure that never rolls pays nothing.
- **The seed never leaves the server.** A client that knew it could compute every later
  roll. The engine keeps it for replay and debugging.
- In a transaction-only procedure, drawing only from `ctx.Random` makes the result fully
  determined by the state, the arguments, the seed and `ctx.Timestamp`.
- In a general procedure the seed reproduces the rolls, but not the outcome: I/O, and what
  other requests committed between your transactions, also matter.
- `ctx.Random` is one sequence. Draw from one logical flow, never from parallel branches.
  Roll up front and pass the values into transaction bodies through args.

---

## 5. Errors

A procedure's errors never decide whether the engine is healthy. There are three tiers:

| Tier | What | The client gets | The server |
|---|---|---|---|
| **Outcome** | Your procedure returned an error: `DbError.Custom(code)`, `DuplicateKey`, any kind at all | the kind and the custom code | nothing logged; the engine and connection are untouched |
| **Request fault** | No such procedure (`UnknownProcedure`); arguments that don't decode (`ProcedureArgsInvalid`); your procedure **threw** (`ProcedureFailed`) | the kind only | logs the exception once (`OnProcedureFault`, default: one stderr line); the connection stays open |
| **Unrecoverable** | The engine itself failed (a WAL fsync, a failed revert...) and poisoned a database | `ServerUnavailable` | exits for the supervisor to restart, see [operations](operations.md) |

- **Only the engine decides the third tier.** Returning `DbError.WalDurabilityFailed()`
  yourself is just an outcome. Nothing inspects a returned kind to judge engine health.
- **In a transaction-only procedure, any error rolls the transaction back.** That holds
  whether you return the error or throw.
- **Clients learn the kind and the custom code, never a message or exception text.**
  Define your own codes (`DbError.Custom(42)`) and localize them on the client.
- **Engine-health kinds all reach clients as `ServerUnavailable`** ("reconnect and
  retry"): `WalDurabilityFailed`, `WalDirectorySyncFailed`,
  `ColdStorageDirectorySyncFailed`, `MultiTxOutcomeUnknown`, `ApplyFailed`,
  `DatabaseClosing`. The server log keeps the real kind.

Route request faults to your own logger:

```csharp
builder.OnProcedureFault(fault => logger.LogError(fault.Exception, "{Procedure} failed for {Connection}", fault.ProcedureName, fault.Session.ConnectionId));
```

### On the wire

```
Rpc        procedureHash u32 | requestId u32 | args
RpcResult  requestId u32 | kind u8 | customCode u16 | value
```

`value` is present only when a general procedure returning `Result<T>` succeeds. Otherwise
the reply is exactly the 7-byte header. `kind` 0 (`None`) is success. An `Rpc` frame shorter than its 8-byte header closes the
connection with `ProtocolError`.

---

## 6. Compile errors

| Id | Meaning |
|---|---|
| RHINO041 | A `[Procedure]` must be `static`. |
| RHINO042 | Invalid signature: no context parameter, a first parameter that is neither `RhinoCtx` nor a `{Db}TxCtx`, the wrong return type, a generic method, a `ref`/`in`/`out` parameter, or a `{Db}TxCtx` naming no database. |
| RHINO043 | A transaction-only procedure that is async, or takes a `RhinoCtx` or `CancellationToken`. |
| RHINO044 | A transaction-only procedure on a keyed Child. Use the general shape. |
| RHINO045 | A parameter or result type that isn't supported, or a `[CustomType]` missing the protocol's serialization attribute. |
| RHINO046 | Two procedures with the same name hash. |
| RHINO047 | A transaction-only procedure that returns a value. Its changes reach clients through views; use the general shape to return a value to the caller. |
