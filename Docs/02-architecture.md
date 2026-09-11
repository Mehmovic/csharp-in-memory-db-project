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

**The actor boundary is per-`[Database]` class, and it reaches all the way
down through storage, not just the in-memory writer.** Every `[Database]`-
attributed `DbContext` subclass gets its own `ColdStore`, and `ColdStore`
wraps exactly one `MdbxEnvironment` — so a database's durability layer is as
independent as its in-memory layer: separate file, separate libmdbx writer
lock, no contention with any other database's commits. (Multiple *tables*
within one database do share that one environment and its one write txn per
`Run` call — see Cold storage below — which is the durable-side mirror of
`DbExecutionLoop` already serializing everything for that `DbContext` through
one channel.) This alignment is a consequence of decisions already made here,
not a separate design: `WidgetDb`/`GameDb`-style multiple `[Database]`
classes are already independently-schedulable actors today, in-process, with
no shared lock at either layer.

This is also the seam a future multi-process deployment would use, should one
ever be needed — not something built or scheduled, just the natural next step
if it is. `Run`/`RunConfirmed` are already the sole entry point into a
`DbContext` (see Embedding & access below), so relocating a given `[Database]`
to its own process would mean swapping what sits behind `Run` — in-process
channel-enqueue becomes an RPC call — without touching the table generator,
the declarative attributes, or the storage boundary at all, since those are
already scoped per-database. Nothing today is built toward this; it falls out
for free from the actor boundary already being per-database rather than
global.

## Transactions

**Status: Milestone 1 of the table generator is real (a single `Instant`-kind
table, primary key only) — secondary indexes, `Persistent`-kind tables, and
real cross-table validation are designed here but not yet built.** This
section describes the target shape; where something is still a stub, it says
so explicitly.

Every `[Table]`-attributed row type gets a generated per-table `Ops`
class; every `[Database]`-attributed class gets a generated `{Db}Transaction`
aggregating one `Ops` field per table. **The database class declares its own
base type as `DbContext<{Db}Transaction>`, by hand** (e.g. `[Database] public
partial class WidgetDb : DbContext<WidgetDbTransaction> { }`) — a source
generator can add members to a partial class, but can never supply the base
type itself, so this one line is the one piece of the generated type's name
the author has to know and write, following the fixed `{Db}Transaction`
naming convention. `DbContext<TTx>` threads that one concrete `TTx` through
every `Run` call on the class with no runtime cast and no per-call generic
parameter — a plain, tx-less `DbContext` (used directly, with no `[Database]`
subclass) is just `DbContext<NullTransaction>` under a convenience
non-generic name, `NullTransaction` being a stateless no-op `ITransaction` so
the tx-less path costs nothing extra. **Tables never write directly — only
read is direct.** An operation's `Insert`/`Update`/`Delete`
calls only ever *stage* a `Change<TKey,TRow>` into that table's `Ops`
(a plain `readonly struct` sitting inline in a `List<Change<TKey,TRow>>` —
zero allocation per staged change); nothing touches real storage until the
whole operation has already returned success, at which point `Transaction
.Apply()` replays every touched table's staged changes in order, inside the
same ambient write-txn machinery cold storage already uses (see Cold storage
below — unchanged by any of this).

**Reads stay direct, and see the same operation's own not-yet-applied
writes.** An `Ops.Get` scans its own staged changes most-recent-first before
falling through to the real `Table`/`PersistentTable` — a `Delete` entry
means "not found," an `Insert`/`Update` entry returns that staged row
directly. This is why a staged `Change`'s key has to be final the moment it's
staged, not resolved later at apply time (see `AutoIncrement` below) — the
overlay has no way to tell two zero-keyed inserts apart within one operation
otherwise.

**Two-phase apply gives free atomicity, no rollback machinery.** `Transaction
.Apply()` checks every dirty table's `Validate()` first — nothing mutates
during this pass — before calling any table's `Apply()`. A validation
failure on the *second* table therefore never leaves the *first* table's
already-staged insert applied, which fixes, for free, the multi-table
partial-apply limitation flagged and deferred earlier in this doc's original
`PersistentTable` design. **Currently a stub**: `Validate()` always returns
`true` — Milestone 1 only proved the staging/overlay/apply-on-success
mechanism itself; real pre-apply validation (duplicate key, unique-secondary-
index conflicts, primary-key immutability — checked across every staged
change before mutating any of them) is the next slice.

**Dirty flags, not a registry.** Each `Ops` class tracks its own `bool
Dirty` (set the moment anything is actually staged — not on a no-op call,
e.g. deleting a key that doesn't exist), and a `Transaction`'s apply logic is
a flat, generator-emitted sequence of `if (table.Dirty) ...` checks — no
dynamic "which tables were touched this call" lookup. A table nothing was
staged against pays one boolean check and nothing else, regardless of how
many tables the database has.

**No stored key-selector delegates anywhere generated.** `Table<TKey,TRow>`
itself still takes a `Func<TRow,TKey> selector` (a hand-written, pre-codegen
type, unchanged by any of this) — but the generator never stores one of its
own. Every generated `Ops.Insert` reads the row's primary-key field directly
(`row.Id`, not `selector(row)`), known statically at compile time — a source
generator, unlike a hand-written generic helper, has no runtime type it
doesn't already know at the point it emits code.

**`AutoIncrement`** — an independent attribute (`[AutoIncrement]`), usually
paired with `[PrimaryKey]` but not required to be. On `Insert`, a zero-valued
tagged field is replaced with the next value from an `AutoIncrementCounter`
*before* staging, via a record-struct `with` expression (every generated
table already assumes `readonly partial record struct`, so this is free).
Assignment happens at stage time, not apply time, for the same overlay
reason above: deferring it would mean two same-operation zero-keyed inserts
both read as key `0` until apply, colliding in the read-your-own-writes scan.
The counter itself lives on the generated database class next to the
table's `Table` field, *not* on the per-operation `Ops` instance — it has to
survive across every `CreateTransaction()` call, not reset each operation.
A value consumed by an operation that later fails and gets discarded is not
reused; gaps are expected and accepted, the same property every real auto-
increment/identity/sequence implementation has (Postgres `SERIAL`, MySQL
`AUTO_INCREMENT` included) — closing that gap would mean serializing every
insert behind a lock held until the whole operation commits, for a guarantee
nothing here needs. **Not yet handled**: `Persistent`-kind tables, where the
counter needs seeding from durable state on restart, or it could reissue an
ID already used by a row that exists cold-only but isn't in the eager-load
set — the same restart hazard already documented under Restart & recovery
below for unique secondary indexes, now extended to auto-incremented primary
keys. `TableGenerator` already skips `Persistent` kind entirely for now, so
this inherits that same scope boundary rather than needing its own.

**Performance discipline, elaborated from [Performance
Principles](01-performance-principles.md):** reading a staged change back —
the overlay scan in `Get`, the replay in `Apply` — binds `ref readonly var c
= ref span[i]` via `CollectionsMarshal.AsSpan(changes)` inside a plain
indexed `for` loop, not `changes[i]` repeated per field accessed and not a
`foreach` copy — a `Change` carries a full row plus a key, and a `List<T>`
indexer returns by value for a struct element type. One real, environment-
specific gotcha worth recording since it cost real debugging time: `foreach
(ref readonly var c in span)` — the *foreach* form specifically, not the
indexed form — triggered a `CS8652 ref struct interfaces ... Preview`
compiler error against this project's pinned Roslyn version, regardless of
target `LanguageVersion` (`Latest`, `CSharp13`, and `CSharp12` all hit it
identically, ruling out a version-resolution explanation). The indexed
`for` + explicit `ref readonly` rebinding form compiles and runs cleanly
and produces the identical zero-copy result, so it's the form every
generated apply/overlay loop uses — worth trying first if this resurfaces.

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

**Confirmed commit batching: mostly already free, the remaining knob is
deliberately deferred, not undesigned.** The async-durability-sync design
above (`ColdStore.EndScope`'s `pendingSync` chaining) already gives `Confirmed`
writes an *opportunistic* form of group commit, not just a way to keep the
writer thread unblocked: `EndScope` chains onto whatever sync is already in
flight rather than firing an independent one, and a sync that runs after a
given commit necessarily covers it — so under concurrent `Confirmed` load,
several callers' commits can end up resolved by the very same
`mdbx_env_sync_ex` call, for free, with no explicit batching code. Under
low/serial load (each `Confirmed` call arriving with idle time before the
next), this property does nothing — every call still pays its own dedicated
fsync, since there's never anything in flight to chain onto.

Closing that gap needs a genuinely new decision, not implied by anything
already built: a **bounded commit-coalescing window** — after a commit
succeeds, wait a small, configurable delay (on the order of 0-2ms) before
actually firing `mdbx_env_sync_ex`, giving other `Confirmed` calls that arrive
in that window a chance to pile onto the same `pendingSync` and share its
cost — the same idea as Postgres's `commit_delay`/`commit_siblings`. This is
deliberately two separate, orthogonal knobs, not one: **txn granularity** (one
write txn per `Run` call, decided, staying that way — bundling unrelated
operations into one txn would couple their atomicity, so a later operation's
failure could roll back an earlier, already-succeeded one nobody asked to
link) versus **sync granularity** (currently opportunistic-only; the
coalescing window is the deliberate lever on top of it). The window's
*existence* as a mechanism is worth deciding now, since it's genuinely hard to
retrofit once application code has been written assuming today's latency
profile; its *default value* stays deferred to
[Roadmap Part H](03-roadmap.md)'s benchmark — same "don't tune blind" posture
used elsewhere in this design — because guessing a number now without a
measured fsync-latency/throughput curve for the actual target hardware would
be worse than not having the knob at all. Why this is safe to defer without
boxing anything in: the entire mechanism is already isolated to one place,
`ColdStore.EndScope`'s sync-firing branch — adding the delay touches nothing
in `DbExecutionLoop`, `PersistentTable`, or any already-shipped
`Insert`/`Update`/`Delete`/`Load`/`Evict`/`Peek` code, the same containment
that already let the original async-sync addendum land without perturbing
anything built before it.

**Secondary-index uniqueness is enforced only against whatever's currently
loaded — the library does not, and today cannot, extend it into libmdbx.** A
unique secondary index is an in-memory structure owned by the composed
`Table`; libmdbx itself is a plain key→blob store keyed by primary key only,
with no concept of a secondary index at all (see "Future, not yet scheduled"
below — that's precisely the layer that doesn't exist yet). Any row that isn't
currently loaded — evicted, never loaded since restart, or simply outside the
eager-load set — sits outside every secondary index's enforcement scope while
its durable copy remains intact in libmdbx. `Evict` is the sharpest case to get
surprised by, because the row was enforced a moment earlier: evict a row held
under a unique username index, then `Insert` a new row reusing that same
username, and the in-memory check passes cleanly (the index has no memory of
the evicted value) and writes through to libmdbx under a different primary
key — libmdbx now durably holds two rows sharing a value the application
believes is unique, and nothing in RhinoDB is aware anything went wrong. This
is not a gap to close by having `Insert` probe cold storage first — the same
reasoning that already rejected that shape for the primary-key case (no
surprise disk read on every `Insert`) applies without exception to every
secondary index. **The responsibility is entirely the caller's**: don't evict
a row governed by a unique secondary index unless the value it held is
independently guaranteed (application-level bookkeeping, or just never
evicting those tables) not to be reintroduced later. Because cold storage is
keyed purely by primary key, there is no way to check for this by secondary
key before inserting, even for a caller who wants to — that only becomes
possible once the relational layer over libmdbx below is built. The hazard's
timing is sharper still under `Optimistic` writes: the colliding `Insert`'s
caller sees success as soon as the in-memory check and enqueue complete, not
after its libmdbx write is durable — so the write that actually corrupts cold
storage can land asynchronously, arbitrarily later and outside the confirm
pipeline of the operation that appeared to succeed.

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

## Restart & recovery

**Recovery is "reopen the environment," not "replay a log."** libmdbx's own
copy-on-write commit model (see Cold storage above) already guarantees cold
storage reflects some prior, fully-committed state after any crash — RhinoDB
doesn't layer a second recovery mechanism on top of it. In-memory state is
never itself persisted (no memory-mapped heap, no snapshot file) — a crash
simply discards it, and a fresh process starts every `Table` completely
empty, the same as a brand-new database would. There is nothing to "recover"
in memory, only cold storage to reopen and, deliberately, *not* bulk-load
from.

**The `OnStart` hook is how a table gets anything back into memory before
traffic arrives, and it runs through the ordinary execution path, not a
special pre-loop step.** Concretely: `OnStart` is scheduled as the first
operation submitted to a `DbContext`'s `Run` pipeline, and hosting code awaits
its completion before accepting external requests — reusing the existing
single-writer serialization (no new concurrency primitive) to guarantee
whatever it loads is visible before anything else runs, for free. What it
loads is entirely application-specific (the roadmap's soccer-manager target
might warm "clubs with an active session," a chat server might warm nothing
at all) — the engine's job is only to provide the primitive (`Load`) and the
guaranteed-first hook point, not to guess what's worth pre-warming for an
application it doesn't know about.

**Restart is the routine, whole-table version of the eviction hazard already
documented above, not a new one.** A unique secondary index only enforces
against currently-loaded rows; after *any* restart, every row outside the
eager-load set is in exactly the state a deliberately-`Evict`ed row is in —
unenforced, until reloaded. Where `Evict` is a deliberate, presumably rare
application action, a restart is routine (every deploy, every crash) and
typically unloads *most* of the table at once, not one row — the same
insert-a-colliding-value corruption already described for `Evict` is not a
one-off edge case here, it's the default post-restart state for anything not
in the eager-load set. **Concrete rule, not just a caution**: a `persistent`
table carrying a unique secondary index that must actually hold should eager-
load its *entire* contents in `OnStart`, not a partial set — partial or no
eager-load is only safe for tables with no unique secondary index (where a
stale value can be wrong to read but can't corrupt a uniqueness guarantee) or
where an application has independently verified staleness there is
acceptable. This isn't a new mechanism to build — `Load` already exists — it's
a stated requirement on how `OnStart` is used per table, worth a comment at
the `[Table]` declaration site once that exists, not just here.

**`Optimistic` writes have a real, bounded data-loss window across a crash —
name it plainly rather than leaving it implied.** An `Optimistic` `Run` call
returns success as soon as its in-memory mutation completes; the libmdbx sync
is fired and forgotten. If the process crashes before that sync completes,
the mutation is gone from cold storage even though the original caller
already observed success and may have already acted on it (sent a network
response, updated a UI). This is the accepted, intentional cost of
`Optimistic` — the whole reason it exists is to not pay for that wait — but it
should be a named, understood property when choosing `Optimistic` vs.
`Confirmed` per call, not a surprise discovered during an incident: a
tick-position update losing at most one in-flight write on a crash is fine;
"player spent 500 gold" is not, and belongs on `Confirmed`.

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
