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
zero copies, for `Apply()`'s in-order replay of every staged change. See
Architecture — Transactions for the one real compiler gotcha this
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

**A third case, first measured 2026-09-14, then fully resolved the same day
once the user decided to build the fix rather than accept the floor.** After
Milestone 5's long-lived-`Ops` fix (above), `RhinoDB.Run.Server.Benchmark`'s
`InstantTableBenchmarks` still showed ~353 B (`Get`) to ~362 B (`Insert`) per
call. Decomposed with two isolation benchmarks rather than assumed:
`SubmissionOverheadBenchmarks.BareTaskCompletionSource` (a bare `new
TaskCompletionSource<Result>(...)` plus its `Task<Result>`, nothing else) came
back at **88 B**; `BareClosureOverEnumAndInt` (a minimal capturing closure over
two primitives) came back at **80 B**. Between them, those two accounted for
most of the floor — `TaskCompletionSource<Result>`+`Task<Result>`, required by
`Run`'s old `Task<Result>`-returning contract, and `DbExecutionLoop.Enqueue`'s
own internal closure (capturing `context`/`operation`/`args`/`mode`/`tcs` to
hand to `channel.Writer.TryWrite`), inherent to "queue a closure onto a
channel" regardless of what the caller's own lambda looked like. A same-day
initial pass confirmed one *free* partial fix: switching call sites to the
already-existing `Run<TArgs>` overload with a `static` lambda (avoiding the
*caller's* own closure) dropped `Get` 353 B → 297 B, `Insert` 361 B → 281 B,
`Update` 321 B → 273 B — real, but still leaving both structural costs above
untouched.

**Built and shipped the same day: a pooled `IValueTaskSource<TValue>`
completion mechanism plus a pooled-work-item channel, replacing
`TaskCompletionSource`/`Channel<Action>` entirely.** One generic
`PooledOperation<TTx,TValue,TArgs> : IValueTaskSource<TValue>,
IExecutionWorkItem<TTx>` (`RhinoDB.Lib/Execution/PooledOperation.cs`) covers
all four `Run`/`Enqueue` overload shapes via a new `IResult<TSelf>` interface
in `RhinoDB.Core` (`IsOk()` + static-abstract `FromError`/`FromException`,
implemented by both `Result` and `Result<T>`) — confirmed boxing-free
(`constrained.callvirt` for the instance member, direct static dispatch for
the static-abstract ones, the same mechanism .NET's own `INumber<T>` uses) and
AOT-safe (value-type generics are already fully specialized under JIT; static
abstract members were explicitly designed for AOT/trimming). Each closed
generic instantiation owns its own `static ConcurrentQueue<PooledOperation<...>>`
free-list — rent on `Enqueue`, configure fields, capture the
`ManualResetValueTaskSourceCore<TValue>`'s version token *before* writing to
the channel (writing first would let the reader recycle the item to an
unrelated caller before the token is read), null the `context`/`operation`
references and return to the pool inside `GetResult` (only after the awaiter
has actually consumed the value — a fire-and-forget `Run` call that's never
awaited simply becomes ordinary garbage, same as it always was). The channel
itself became `Channel<IExecutionWorkItem<TTx>>` — a cheap interface reference
to an already-rented, reused object, not a fresh closure; precisely *not* a
literal value-type channel element, since a struct-by-value element couldn't
support `IValueTaskSource`'s in-place completion mutation. `Run`/`RunConfirmed`
(`DbContext<TTx>` and non-generic `DbContext`, 16 methods total) now return
`ValueTask<Result>`/`ValueTask<Result<T>>` — a deliberate, real breaking API
change from `Task`, needed because keeping `Task` as the public type while
pooling internally would force a real `Task` allocation via `.AsTask()` on
every call, defeating the entire point.

**Two real, non-obvious correctness gotchas hit and fixed during
implementation, both concrete illustrations of `ValueTask`'s contract being
genuinely different from `Task`'s, not just a rename:**
- `ValueTask<T>` is **single-consumption** — awaiting or calling
  `.GetAwaiter().GetResult()` a second time on the same `ValueTask<T>` throws
  `InvalidOperationException`, unlike `Task<T>`, which happily returns the
  same cached value to as many callers as ask. Proven directly by
  `PooledOperationTests.AfterConsumption_ASecondConsumptionOfTheSameValueTaskThrows`.
  **Any code storing a `ValueTask`, awaiting it more than once, or checking
  `.IsCompleted` before consuming it, must stop — await it (or call
  `.AsTask()` first) exactly once.**
- **`ValueTask<T>` does not support a blocking synchronous wait the way
  `Task<T>` does.** `Task<T>.GetAwaiter().GetResult()` blocks the calling
  thread until the task completes if it isn't done yet; a bare
  `ManualResetValueTaskSourceCore<T>`-backed `ValueTask<T>`'s `GetResult`
  does **not** block — it assumes the proper `await`/`OnCompleted`
  coordination already happened, and throws `InvalidOperationException:
  Operation is not valid due to the current state of the object` if called
  too early. This broke three benchmark `[GlobalSetup]` methods
  (`InstantTableBenchmarks`, `PersistentTableBenchmarks`,
  `ConfirmedCoalescingBenchmarks`) that did
  `db.Run(...).GetAwaiter().GetResult();` synchronously right after
  enqueueing — invisible to the whole test suite beforehand, since every test
  either used proper `await` or drove the operation to completion before
  consuming it. Fixed by inserting `.AsTask()` before the blocking wait at
  each site (`.AsTask()` produces a real `Task<T>`, which does support
  blocking wait) — **the same fix any RhinoDB-consuming code needing a
  genuine synchronous block on `Run`'s result must apply.**

**Measured result** (`SubmissionOverheadBenchmarks`, `InstantTableBenchmarks`,
same methodology as the original decomposition): `PooledRunNoOp` (the new
mechanism's full real end-to-end submission cost — cross-thread channel write,
pooled rent, actual await, no table work) landed at **80 B**, lower than the
*old* mechanism's bare `TaskCompletionSource` component alone (88 B).
`Get` 353 B → **152 B**, `Insert` 361 B → **176 B**, `Update` 321 B → **136 B**,
`GetArgs` 297 B → **96 B**, `InsertArgs` 281 B → **96 B**, `UpdateArgs`
273 B → **80 B** — a 50-71% reduction across the board, flat across all three
`RecordCount` tiers, wall-clock time unchanged (~3-3.5 μs). The residual
~80-96 B floor is very likely `ExecutionContext` flow across the cross-thread
continuation (a cost intrinsic to *any* cross-thread `await`, `Task`-based or
not — not attributable to this design specifically) — not decomposed further
without real profiling tools, matching this project's own "don't optimize
blind" posture applied consistently throughout. `TableGenerator.cs` required
zero changes — `CreateTransaction()`'s cached-singleton behavior was already
untouched by this redesign, confirmed by a zero-diff check at the end.

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
