# Architecture

This is the core model, kept current as the repo's own source of truth (see
[Overview](00-overview.md) for why these `Docs/` files, not an external
artifact, are authoritative). Working pseudocode for anything already built
lives in the actual source — `src/RhinoDB.Lib/Execution/` for the transaction
pipeline, `src/RhinoDB.Lib/Storage/` and `Indexing/` for the storage engine —
not reconstructed here.

## Table kinds

Exactly two, and only two:

- **`persistent`** — durable, libmdbx-backed. Can only be written inside an open
  transaction — enforced at compile time via a distinct type (`PersistentTable<T>`
  only exposes write methods when transaction-gated), not a runtime check.
- **`instant`** — in-memory only. Can be written either inside a transaction or
  directly.

Declaring a table only settles durability. There's no separate transaction-opening
API layered on top of that — every `DbContext.Run(operation)` call is already
atomic for everything `operation` touches, regardless of which table kinds it
touches (see Execution model below), so a dedicated `BeginTransaction()`/`.Atomic`/
`transient` surface would add nothing the unified call point doesn't already give
for free. What *is* a per-call decision is `PropagationMode` (`Optimistic`,
default, vs. `Confirmed`), a parameter on `Run` itself: `Optimistic` returns as
soon as the in-memory change is applied, `Confirmed` will (once Stage 5/6 exist)
wait for the libmdbx commit before returning. Both behave identically today —
there's no libmdbx commit yet to differentiate them against.

**Capacity:** `growable` is the default — a table grows as needed via the
chunked storage described below. `fixed(n)` is a deliberate opt-in for a table
tied to a real, provisioned ceiling (a single-row config table, a hard
player-count cap): an insert past `n` is a defined, immediate error rather than
the table quietly consuming memory until something unrelated fails later. A
table-level cap protects every caller, including ones written after the table
was declared — an application-level check elsewhere only ever guards the one
code path it was written for.

## Execution model: async at the edges, single-writer at the center

The unit of work is one call point: `DbContext.Run<T>(Func<DbContext, Result<T>>
operation)` — no separate reducer/procedure/view types. What makes collapsing
those into one entry point safe is the delegate's shape: `operation` is
**synchronous**, never `Func<DbContext, Task<Result<T>>>`, so there is no `await`
possible inside it and therefore no way to block the writer thread on I/O.
That's the actual hazard other actor-style databases split reducers and
procedures to avoid — closing it off at the type level means RhinoDB never
needed the second entry point to begin with.

Every `Run` call is submitted to a single-consumer `Channel` and processed one
at a time by a `DbExecutionLoop` — there is only ever one writer, system-wide,
and it is also the only reader. Internal reads (a table accessor called inside
`operation`) and external reads (a subscription's initial snapshot and its
subsequent diffs) are both computed on that same thread, at the same
serialization point as writes. No table-level locking exists or is needed — a
table is only ever touched by the one thread that owns its `DbContext`, so
`Table<TPk,TRow>` carries no concurrency primitives of its own.

This supersedes a correction made 2026-09-01, which had `ApplyInMemory` acquire
every table a transaction touches up front (not one at a time) to stop a reader
from observing a multi-table transaction half-applied. That fix was patching a
problem specific to readers running independently of the writer via per-table
locks; once reads no longer do that, the torn-read hazard it corrected doesn't
exist, and neither does the lock it was built around. A multi-table `operation`
is now just sequential calls against tables in the same `DbContext` — atomic for
free, no lock acquisition of any kind.

Two *separate*, separately-awaited `Run` calls are a different story: nothing
pins state between them, so other operations can and will interleave in
between. If two things need to be atomic together, they belong inside one
`operation`, not two sequential `await Run(...)` calls.

`DbContext` is the actor: one `DbExecutionLoop`, one `Channel`, and whichever
tables — persistent or instant, the durability kind is irrelevant here, see
Table kinds above — are registered to it. Scaling is horizontal, not
intra-process: parallelism comes from running multiple independent `DbContext`s,
each fully serial internally, coordinating with each other, if at all, only
through the async change-propagation channel below, never a shared lock. One
database is one actor; more scale means more databases, not more concurrency
inside one.

## Storage engine (in-memory)

See [Performance Principles §1](01-performance-principles.md#1-data-oriented-not-object-oriented)
for the data-oriented rationale. Concretely: `DenseArray<TRow>` is the physical
storage per table, `Dictionary<TKey,int>`-backed indexes map keys to physical
offsets directly, and `Table<TPk,TRow>` is the coordinator that owns the array
and drives every index. The primary index (`IUniqueIndex<TPk>`) is a constructor
argument; secondary indexes are added via `Register(ISecondaryIndex<TRow>)` at
setup time, then driven per-operation through a uniform `CheckInsert`/`Insert`/
`Delete` contract regardless of whether the concrete index is unique or
non-unique — type-erased via `UniqueSecondaryIndex<TRow,TKey>`/
`NonUniqueSecondaryIndex<TRow,TKey>` wrappers. This uniform contract is what lets
a delete's swap-remove correctly repoint *every* index on the table, not just the
one that initiated the delete, and what lets `Update`'s always-re-register pass
avoid a false self-collision (`CheckInsert(row, selfOffset)` — a found entry is
only a real conflict if it belongs to a different offset than the row's own).

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
in-memory at this scale. `HashIndex` gives O(1)-average exact match but no range/
prefix; `OrderedIndex` gives O(log n) exact match *and* range/leading-column-prefix
queries — worth being direct about the temptation to keep both on the same column
for O(1) point lookup alongside range support: usually not worth it, since
`OrderedIndex`'s O(log n) lookup is already cheap in practice (roughly 17
comparisons on a 100,000-row table) and rarely earns back doubled write-maintenance
cost. Default to one index type per column, matched to its real query shape. If a
range index ever becomes a proven hot path, the concrete upgrade target is a
cache-sensitive B+tree variant — see the Backlog entry in
[Roadmap](03-roadmap.md#backlog-deferred-not-scheduled) for the specific shape.

Physically, this is the same pattern known outside databases as a *sparse set* or
*slot map*: the dense array is the data, the `Dictionary<TKey,int>` is purely an
index into it, never the storage itself — used wherever both fast point lookup
and fast full-iteration are needed from the same structure.

## Embedding & access

RhinoDB can be linked directly into a process, or reached over a network — that's
a deployment choice, not something the database has an opinion about. But the two
paths do not carry the same consistency guarantee, and the API doesn't pretend
otherwise: **embedded** access is synchronous and reads current committed state
directly, no staleness window. **Networked** access is async — a read is a
snapshot by the time it arrives, a write is write-back, not immediate. Forcing
the embedded path to also behave like a stale-snapshot/async-write-back interface,
purely so one interface could describe both, would quietly downgrade the actual
reason embedding is worth doing (synchronous, zero-latency access) to buy a
uniformity nobody asked for. Both share the same method shape, so retargeting
embedded → networked is a wiring change, not a rewrite — but each is documented
with the guarantee it actually has, not a shared lowest common denominator.

## Cold storage

libmdbx (embedded KV store, B+tree, copy-on-write, single-writer/multi-reader) is
the durability layer for `persistent` tables. RhinoDB owns serialization directly
(fast/positional MemoryPack, no version tags — a migrated sub-database is always
uniform in shape). In-memory is authoritative for whatever's currently loaded;
libmdbx is the durability mechanism and the store for everything not currently
loaded. Serialization stays two formats, not one: memory and libmdbx's stored
bytes share an encoding, since libmdbx's format is entirely user-controlled — but
a future network wire format (for a client accessing RhinoDB remotely) is kept
separate, since it carries requirements (cross-machine endianness, tolerance for
schema drift between client/server versions) the memory/storage format doesn't
need to take on.

**Precision: libmdbx doesn't give you a write-ahead log.** It gets durability
from copy-on-write instead — a write lands on fresh, unused pages, and a commit
is one atomic swap of the root pointer. A crash mid-write just leaves the old
root, and everything reachable from it, intact; the half-written pages are never
referenced by anything. RhinoDB doesn't additionally hand-roll a second log of
its own — libmdbx's own mechanism is the only one, so there's nothing to
disagree with itself after a crash.

`Load`/`Evict`/`Peek` are distinct from `Insert`/`Delete` — they move data between
memory and libmdbx without changing the durable data itself, so they produce no
change-propagation entry. `Insert`/`Delete` write through to libmdbx because the
row's actual state changed; `Load`/`Evict` touch memory only, and `Peek` is a
read-only point lookup straight into libmdbx that returns a row's current value
*without* loading it into memory — so a broad query (a leaderboard scan over
offline accounts) doesn't force every row it touches into the working set just to
read it. Recovery starts empty except for a small, fixed, eagerly-loaded set on
startup (the `OnStart` hook) — no bulk warm-up.

**`Evict` must wait for its table's last `Optimistic` commit before dropping a
row from memory.** "Every committed write already reached libmdbx by eviction
time" holds unconditionally for `Confirmed` writes, but not for `Optimistic`
ones — their commit is fired and forgotten, so it can still be in flight when a
row is evicted moments later. If a subsequent `Load` for that same row then reads
libmdbx before that commit lands, its MVCC snapshot won't see it (reads never
wait on in-flight writes — that's what makes them lock-free). Each persistent
table tracks only its most recent fire-and-forget commit (never per-row —
libmdbx's own writer lock already serializes real commits in true order, so a
later one finishing guarantees every earlier one on that table already has too),
and `Evict` awaits it before removing the row. `Confirmed` commits never need
tracking at all — their own await already provides a stronger guarantee before
propagation even runs.

**Schema migration: no `ALTER TABLE`, because there's no catalog.** libmdbx is
schema-blind — a key maps to an opaque blob, and the only place a schema exists
at all is in RhinoDB's own serialization code. A migration can't be declarative;
it's an imperative routine: decode old bytes, produce the new shape, write it
back. Scoped to the table, not the whole store — each table lives in its own
named sub-database within a shared libmdbx environment, so a shape change only
migrates that one sub-database (read every row under the old shape, transform,
write into a new sub-database under the new shape), leaving every other table
untouched. Batched into a handful of transactions, not one commit per row — a
real fsync per row versus per batch is the difference between seconds and tens
of minutes over a million rows. The old sub-database is dropped afterward,
returning its pages to the same environment for reuse. Versioning lives entirely
outside libmdbx, as generated code in source control: a source generator tracks
each row struct's last-known shape, and when the live struct changes, emits a
frozen, compilable snapshot of the old shape plus a stub transform function for
whatever a mechanical add/remove can't infer — "which sub-database is current"
is never looked up at runtime, the compiled code already knows.

**Future, not yet scheduled — a relational layer over libmdbx.** The surface
today is deliberately narrow: load and offload by primary key, nothing else. The
same shape CockroachDB/TiDB/FoundationDB's own layers use over their KV
substrates would extend this — a secondary index as extra KV pairs (indexed
value → primary key) in their own sub-database, written in the same transaction
as the row so they can't drift apart; `MDBX_DUPSORT` (multiple sorted values per
key) is a natural fit for a non-unique index without hand-rolling a composite
key. This mirrors the in-memory index layer's own key→offset indirection almost
exactly (secondary DBI → primary key → primary DBI, versus hash/ordered index →
array offset → `DenseArray`), so no new index *concept* is needed when this gets
built — just cursor support in the native binding and an order-preserving key
encoding. Deliberately deferred: nothing durable exists yet to backfill an index
over.

## Change propagation (designed, not yet built)

Every mutation enqueues a change (table, key, kind, captured value) inline at
commit time — ordering falls out of single-writer serialization for free, no
separate sequencing mechanism needed. Delivery guarantee (`Reliable` vs.
unordered/lossy) is a per-table declaration, like durability tier — not a per-call
choice like `Optimistic`/`Confirmed`. A per-connection sender loop drains its own
outgoing channel with opportunistic merge (last-write-per-key wins within an
in-flight batch); no field-level diffing is planned.

This is also the only bridge between two `DbContext`s — there is no synchronous
cross-context transaction; anything spanning two database instances is async,
message-passing coordination, never a shared lock.

For an instant-only change, or an `Optimistic` transaction, propagation is
immediate — enqueued right after the in-memory mutation, not waiting on a
libmdbx write. For a `Confirmed` transaction, propagation waits until *after*
its commit actually succeeds — otherwise a subscriber could be told about a
change that then fails to durably land, exactly what `Confirmed` exists to
prevent. This costs nothing extra: `Confirmed` already blocks the single-writer
loop on that same await, so nothing else can interleave regardless of when
propagation happens within it. See [Roadmap Stage
8](03-roadmap.md#stage-8--viewtable-only-client-access-subscribe-and-diff) for
how this becomes the basis of a subscribe-once/diffs-after client access model.

## Hosting

Console app first — no networking until the core engine is solid. Later, a minimal
direct HTTP/WebSocket host (mature libraries used as-is, not reinvented) rather
than a full framework, to avoid paying for controller/DI/middleware overhead that
this project has no use for.

## Deployment

RhinoDB is a library you embed, not a service you connect to — you build and run
your own instance, and RhinoDB doesn't host anyone else's code or decide your
topology for you. One instance, several instances in one process, or several
processes behind your own gateway are all legitimate; RhinoDB has no opinion
about which, because it isn't a hosting platform that would need one.

## Error handling

`Result`/`Result<T>` instead of throwing on expected/common failure paths (not
found, duplicate key) — chosen specifically because `out` parameters are illegal in
`async` methods, and much of the outer API (`DbContext.Run`) is necessarily async.

The error a `Result` carries is `DbError`: a slim `readonly struct` holding
nothing but a `Kind` (a generated, `byte`-backed enum — one member per
`[GenerateDbError]`-tagged exception class, e.g. `DuplicateKey`,
`IndexKeyNotFound`, `OffsetNotRegistered`, `OffsetOutOfRange`,
`PrimaryKeyImmutable`). No key, offset, or any other per-call data rides along —
those exceptions are parameterless with a fixed generic message
(`DuplicateKeyException() : Exception("Key already exists")`), and `DbError`'s
generated `ToException()` builds a fresh instance from `Kind` on demand. This is
deliberate: `DbError` represents *anticipated* logic failures the caller is
expected to branch on, not a bag of debug context.

One `Kind` breaks that pattern on purpose: `SystemFailure`, produced by
`DbError.SystemFailure(Exception)`, is the only one that actually carries data —
the real exception a `DbContext.Run` operation didn't catch itself. Everything
thrown out of an operation ends up here rather than faulting the returned `Task`,
so callers get one uniform `Result`-shaped answer instead of needing both an
`IsError()` check and a `try`/`catch` at every call site — while `Kind ==
SystemFailure` keeps an unplanned failure visibly distinct from an ordinary
`DuplicateKey` rejection. `Result`/`Result<T>` both also expose a convenience
`Error(Exception)` overload that wraps into `SystemFailure`, so an operation that
catches its own exception can report it identically to one that doesn't.
