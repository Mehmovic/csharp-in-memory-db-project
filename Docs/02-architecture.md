# Architecture

This is a summary of the core model. For pseudocode and worked examples, see the
live design artifact linked from [Overview](00-overview.md).

## Table kinds

Exactly two, and only two:

- **`persistent`** — durable, libmdbx-backed. Can only be written inside an open
  transaction — enforced at compile time via a distinct type (`PersistentTable<T>`
  only exposes write methods when transaction-gated), not a runtime check.
- **`instant`** — in-memory only. Can be written either inside a transaction or
  directly.

Declaring a table only settles durability. Transactionality is a separate, per-call
decision layered on top:

- `ctx.BeginTransaction()` opens an explicit transaction, persistent or mixed.
- `.Atomic` gives a single-operation write to a persistent table a no-ceremony path
  (opens+commits a transaction internally for that one op).
- `transient` is a transaction opened purely around `instant` writes — in-memory
  atomicity/isolation for that call, nothing recorded on crash.

Two propagation modes apply to a transaction's outward-facing behavior:
`Optimistic` (fire-and-forget libmdbx commit, propagate to subscribers immediately)
vs. `Confirmed` (await the libmdbx commit before propagating).

## Execution model: async at the edges, single-writer at the center

The unit of work is one call point: `Context.Run<T>(Func<Context, Result<T>>
operation)` — no separate reducer/procedure/view types. What makes collapsing
those into one entry point safe is the delegate's shape: `operation` is
**synchronous**, never `Func<Context, Task<Result<T>>>`, so there is no `await`
possible inside it and therefore no way to block the writer thread on I/O.
That's the actual hazard other actor-style databases split reducers and
procedures to avoid — closing it off at the type level means RhinoDB never
needed the second entry point to begin with.

Every `Run` call is submitted to a single-consumer `Channel` and processed one
at a time by a `SingleWriterLoop` — there is only ever one writer, system-wide,
and it is also the only reader. Internal reads (a table accessor called inside
`operation`) and external reads (a subscription's initial snapshot and its
subsequent diffs) are both computed on that same thread, at the same
serialization point as writes. No table-level locking exists or is needed — a
table is only ever touched by the one thread that owns its `Context`, so
`Table<TPk,TRow>` carries no concurrency primitives of its own.

This supersedes a correction made 2026-09-01, which had `ApplyInMemory` acquire
every table a transaction touches up front (not one at a time) to stop a reader
from observing a multi-table transaction half-applied. That fix was patching a
problem specific to readers running independently of the writer via per-table
locks; once reads no longer do that, the torn-read hazard it corrected doesn't
exist, and neither does the lock it was built around. A multi-table `operation`
is now just sequential calls against tables in the same `Context` — atomic for
free, no lock acquisition of any kind.

Two *separate*, separately-awaited `Run` calls are a different story: nothing
pins state between them, so other operations can and will interleave in
between. If two things need to be atomic together, they belong inside one
`operation`, not two sequential `await Run(...)` calls.

`Context` is the actor: one `SingleWriterLoop`, one `Channel`, and whichever
tables — persistent or instant, the durability kind is irrelevant here, see
Table kinds above — are registered to it. Scaling is horizontal, not
intra-process: parallelism comes from running multiple independent `Context`s,
each fully serial internally, coordinating with each other, if at all, only
through the async change-propagation channel below, never a shared lock. One
database is one actor; more scale means more databases, not more concurrency
inside one.

## Storage engine (in-memory)

See [Performance Principles §1](01-performance-principles.md#1-data-oriented-not-object-oriented)
for the data-oriented rationale. Concretely: `DenseArray<TRow>` is the physical
storage per table, `Dictionary<TKey,int>`-backed indexes map keys to physical
offsets directly, and `Table<TRow>` is the coordinator that owns the array and
drives every index (primary and secondary alike) through `Register`/`Deregister` —
this is what lets a delete's swap-remove correctly repoint *every* index on the
table, not just the one that initiated the delete.

Indexes are declared explicitly per table, never defaulted — each one is a real
cost (write-path maintenance), so it's a decision, not a default:

- **Hash** (`Dictionary`-backed) for exact-match, unique or non-unique.
- **Ordered** (`SortedSet`-backed) for range/prefix queries, unique or non-unique.
- Composite (multi-field) keys work out of the box via `ValueTuple`'s structural
  equality/ordering — leftmost-prefix semantics, not an independent bounding box on
  each field.

A literal B+tree and a spatial index (quad-tree/R-tree/geohash) are both explicitly
**not** planned unless a specific table's measured profile proves the simpler tools
insufficient — RhinoDB's tables are small and fragmented (not heavily normalized),
so the usual justification for those structures (minimizing pointer-chasing against
slow storage, or answering true 2D bounding-box queries) mostly doesn't apply
in-memory at this scale.

## Cold storage

libmdbx (embedded KV store, B+tree, copy-on-write, single-writer/multi-reader) is
the durability layer for `persistent` tables. RhinoDB owns serialization directly
(fast/positional MemoryPack, no version tags — a migrated sub-database is always
uniform in shape). In-memory is authoritative for whatever's currently loaded;
libmdbx is the durability mechanism and the store for everything not currently
loaded.

`Load`/`Evict`/`Peek` are distinct from `Insert`/`Delete` — they move data between
memory and libmdbx without changing the durable data itself, so they produce no
change-propagation entry. Recovery starts empty except for a small, fixed,
eagerly-loaded set on startup — no bulk warm-up.

## Change propagation (designed, not yet built)

Every mutation enqueues a change (table, key, kind, captured value) inline at
commit time — ordering falls out of single-writer serialization for free, no
separate sequencing mechanism needed. Delivery guarantee (`Reliable` vs.
unordered/lossy) is a per-table declaration, like durability tier — not a per-call
choice like `Optimistic`/`Confirmed`. A per-connection sender loop drains its own
outgoing channel with opportunistic merge (last-write-per-key wins within an
in-flight batch); no field-level diffing is planned.

This is also the only bridge between two `Context`s — there is no synchronous
cross-context transaction; anything spanning two database instances is async,
message-passing coordination, never a shared lock.

## Hosting

Console app first — no networking until the core engine is solid. Later, a minimal
direct HTTP/WebSocket host (mature libraries used as-is, not reinvented) rather
than a full framework, to avoid paying for controller/DI/middleware overhead that
this project has no use for.

## Error handling

`Result`/`Result<T>` instead of throwing on expected/common failure paths (not
found, duplicate key) — chosen specifically because `out` parameters are illegal in
`async` methods, and much of the outer API (`BeginTransaction`, `Confirmed`
propagation) is necessarily async. A `Result` carries the real exception object
(not a string or error code), deferring the throw rather than paying its cost,
while preserving type-based discrimination whenever the caller does ask for it.
Custom typed exceptions (`DuplicateKeyException`, `IndexKeyNotFoundException`,
`OffsetNotRegisteredException`, `PrimaryKeyImmutableException`) replace generic BCL
exceptions across the index layer, each carrying the offending key/offset as a
real property, not just a formatted message.
