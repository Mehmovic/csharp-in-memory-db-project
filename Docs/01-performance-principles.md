# Performance Principles

RhinoDB exists to sit on a hot path (a game server tick) where GC pauses and
unpredictable allocation are the enemy, not just "slow." These principles apply to
every layer of the engine, not just the parts that look performance-critical at
first glance. They're not aspirational — they're a decision filter: if a proposed
design violates one of these, that's a reason to reject it or redesign it, not a
detail to fix later.

## 1. Data-oriented, not object-oriented

Storage is a dense, packed struct array per table — no gaps, no tombstones, no
per-row heap object. Rows are structs; a table's storage is contiguous memory a CPU
can stream through, not a graph of scattered heap allocations linked by references.

- `DenseArray<T>` — the physical layer. Insert appends, delete swap-removes (the
  physically-last row moves into the vacated slot instead of leaving a hole),
  growth is chunked with hysteresis (grow when full, only free a trailing chunk once
  population drops well below capacity) so churn near a boundary doesn't thrash.
- Indexes (`HashIndex`, `OrderedIndex`, and their non-unique siblings) map a key
  directly to a physical offset in that array — never through an extra indirection
  hop. Every index — primary or secondary — is one hop from key to row.
- Composite keys (e.g. `(X, Y)`) work for free through `ValueTuple`'s structural
  equality/ordering — no special-cased composite-key machinery.

This pattern has a name outside databases too — a *sparse set*, or *slot map* —
used for exactly this dual-access reason (fast point lookup **and** fast full
iteration from the same structure) wherever it shows up.

The payoff: a full table scan, a range query, or a hot-path write touches
contiguous memory and predictable index structures, not a pile of individually
allocated objects the GC has to track.

## 2. No boxing

A `TKey`/`TRow` typed as `object` anywhere on a hot path is a rejected design, full
stop — it means every struct key or row gets heap-allocated just to be looked at.
Every index type, every storage type, is generic over the real key/row type, all
the way down. This was violated once, in an early sketch of the transaction
pipeline (`Change.Key`/`Value` typed `object`) — caught in review and corrected to
a generic `Change<TKey,TRow>` before being built. See
[Architecture — Transactions](02-architecture.md#transactions).

The same discipline applies to *reading* a `Change` back, not just storing one:
a `List<Change<TKey,TRow>>` indexer returns a struct element by value, so
`changes[i]` repeated per field examined (or a plain `foreach`) silently copies
the whole row on every access. The generated table code binds `ref readonly var
c = ref span[i]` via `CollectionsMarshal.AsSpan(changes)` instead — one binding,
zero copies, whether it's the read-your-own-writes overlay scan or the apply-time
replay. See Architecture — Transactions for the one real compiler gotcha this
surfaced (a `foreach (ref readonly ...)` form that doesn't compile in this
project's pinned Roslyn version, where the equivalent indexed `for` loop does).

## 3. No closures on the hot path

A capturing lambda allocates a heap object to hold what it captured, every time
it's constructed. That's invisible in normal C# code and fatal on a per-tick path.
The rule is specifically about *capturing* closures — a `static` non-capturing
lambda (`static (ref TRow u) => { u.X = x; }`) is cached by the Roslyn compiler
after its first use and costs nothing per call, so "avoid closures" does not mean
"avoid delegates."

Two places this bit us, both caught in design review rather than after being built:

- The transaction pipeline's original `BufferedOps` was `List<(TableId, Action)>` —
  an `Action` closure allocated per buffered operation. Fix, now built exactly
  this way: per-table typed `Ops` buffers instead of `Action` (see
  [Architecture — Transactions](02-architecture.md#transactions)).
- An early sketch had `Update`'s user-facing shape as `u => u with { X = x }` —
  copies the row *and* allocates a capturing closure. What actually shipped is
  simpler than either that or the mutator-delegate fix once floated as an
  alternative: `Update(TKey id, TRow newRow)` takes the complete replacement row
  directly, no delegate parameter of any kind. Whatever the caller does to build
  `newRow` (a `with`-expression against an already-loaded row is the common case)
  is the caller's own cost, off the generated API surface entirely — there was
  never a closure to eliminate from the call itself once the shape stopped
  routing through a delegate at all.

**A third case, resolved 2026-09-14 by measuring rather than guessing further:
`DbContext.Run`'s own entry point allocates a small, bounded, and now-accepted
amount per call — this is the contract, not an unclosed gap.** After Milestone
5's long-lived-`Ops` fix (above), `RhinoDB.Run.Server.Benchmark`'s
`InstantTableBenchmarks` still showed ~353 B (`Get`) to ~362 B (`Insert`) per
call. Decomposed with two isolation benchmarks rather than assumed:
`SubmissionOverheadBenchmarks.BareTaskCompletionSource` (a bare `new
TaskCompletionSource<Result>(...)` plus its `Task<Result>`, nothing else) came
back at **88 B**; `BareClosureOverEnumAndInt` (a minimal capturing closure over
two primitives) came back at **80 B**. Between them, those two — both
structural to the current single-writer channel design, not incidental bugs —
account for most of the floor:

- **`TaskCompletionSource<Result>` + its `Task<Result>` (~88 B)**: required by
  `Run`'s public `Task<Result>`-returning contract. Removing this needs a
  `ValueTask` + pooled `IValueTaskSource<Result>` redesign — a real, known
  high-performance .NET pattern (the same one Kestrel/`System.IO.Pipelines`
  use), but a genuinely bigger, separately-scoped change, not a small fix.
- **`DbExecutionLoop.Enqueue`'s own internal closure (~100-150 B for its 4-5
  captures — `context`/`operation`/`args`/`mode`/`tcs`)**: the `Action` handed
  to `channel.Writer.TryWrite` must capture all of these per call regardless of
  what the *caller's* lambda looks like — this is inherent to "queue a closure
  onto a channel," not something a caller-side fix can touch. Removing it needs
  `Channel<Action>` restructured into `Channel<TWorkItem>` where `TWorkItem` is
  a struct holding the same fields by value — a real design (a `Channel<T>`
  with a struct `T` doesn't allocate per enqueued item, the same "amortized,
  chunked" reasoning already used for `DenseArray<T>`), but again bigger than
  a one-line fix, and untried.

**What *is* a free, already-available fix, and was confirmed by measurement**:
a *caller's own* capturing closure is avoidable today, with zero engine
changes, via the `Run<TArgs>`/`Run<T,TArgs>` overloads that already exist
specifically for this (`ctx.Run(static (db, tx, args) => ..., args)`).
Benchmarked side by side (`GetArgs`/`InsertArgs`/`UpdateArgs` next to
`Get`/`Insert`/`Update`): switching to a `static` lambda + explicit `args`
dropped `Get` 353 B → 297 B, `Insert` 361 B → 281 B, `Update` 321 B → 273 B —
a genuine, measured 50-80 B (15-22%) reduction, matching this principle's own
guidance that a `static` non-capturing lambda costs nothing to construct.
**Recommendation for any RhinoDB-consuming code with per-call captured state
(an id, a value, a key)**: prefer `Run<TArgs>` with a `static` lambda over the
plain `Run` overload with an implicit capture — cheap to apply, no downside.

**Decision: the remaining ~270-300 B floor is accepted as the contract, not
pursued further right now.** In absolute terms this is small — Gen0 collection
of a few hundred bytes per multi-microsecond operation is not a measurable
tick-latency concern, and both remaining structural costs would need their own
dedicated, riskier redesigns to remove. Matches this project's standing
"don't optimize blind" posture (the same gate already applied to the
`ArrayPool`-for-`changes` idea and the CSB+-tree backlog entry): revisit if
Stage 9+'s actual game workload profiling ever shows this floor mattering in
practice, not before. `SubmissionOverheadBenchmarks.cs` and the `*Args`
comparison benchmarks stay in the benchmark project as living measurements,
not deleted once the question was answered.

## 4. No LINQ on the hot path

Iterator-based LINQ (`.Where()`, `.Select()`, `GroupBy`, etc.) allocates enumerator
objects and closures internally. Where a LINQ pipeline would be reached for, we use
a plain loop or a plain `Dictionary`-based grouping instead — not because LINQ is
never appropriate anywhere in the codebase, but because it's never appropriate on a
path a game tick can hit.

## 5. Source generators are how we get all of the above *and* a nice API

None of the above is worth much if using RhinoDB is miserable. The resolution is
codegen, not compromise: an incremental Roslyn source generator reads a declarative
table definition (which fields, which indexes, what kind) at compile time and emits
the actual typed, non-boxing, closure-free code — index maintenance, per-table
accessor structs, and (per the 2026-09-01 correction) the generic `Change<TKey,TRow>`
transaction types.

This is the intended shape for *all* of it, not just index maintenance:

- **Index maintenance** — the generator knows at compile time which index depends
  on which field(s), so `Update` only touches the indexes whose backing field
  actually changed, instead of blindly re-registering everywhere. **Done
  2026-09-12**: `Ops.Apply()`'s `Update` case compares each index's key
  expression on the old vs. new row via `.Equals()` and skips the
  Delete+Insert pair when unchanged — see Docs/02-architecture.md § Storage
  engine for the historical self-collision bug this had to avoid (an early
  attempt apparently skipped under the wrong condition and left a stale
  entry behind).
- **Per-table accessors** — a generated `Ops` class (`WidgetOps`, `ClubOps`, ...)
  owns a `DenseArray<TRow>` field and a concrete primary/secondary index field
  per index directly (no generic coordinator type in between — see
  [Architecture § Storage engine](02-architecture.md#storage-engine-in-memory)),
  constructed once per database, not recomputed per access.
- **Transaction change types** — the generator emits one `Ops` class per table and
  one `Transaction` class per database, both built on the shared, generic
  `Change<TKey,TRow>`, so the call site (`tx.Widgets.Update(...)`) never has to
  touch `object` or a hand-written `Action` closure underneath.

Reflection is rejected outright, everywhere, for the same underlying reason: it's
slow and its cost is invisible at the call site. Plain generics
(`Table<TRow>`, distinct `PersistentTable<T>`/instant table types) plus source
generators give us compile-time-checked, allocation-free code without ever paying
reflection's runtime cost.

## How this gets applied day to day

When a new piece of the design is sketched, it gets checked against this list
before it gets built — not after. Two real violations were caught this way during
the 2026-09-01 design review (the `object`/`Action`-typed transaction pipeline, and
the `u => u with {...}` closure in `Update`) and corrected before a line of
production code was written for them. That's the intended failure mode: catch it
on paper, not in a profiler six months from now.
