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
  actually changed, instead of blindly re-registering everywhere. **Not yet
  built** — the first generated slice always re-registers, matching
  `Table<TKey,TRow>`'s own unconditional behavior; deferred until profiling
  shows it matters, not assumed to matter up front.
- **Per-table accessors** — a generated `Ops` class (`WidgetOps`, `ClubOps`, ...)
  wraps a real, non-generic `Table<TKey,TRow>`/`PersistentTable<TKey,TRow>`
  reference, constructed once per database, not recomputed per access.
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
