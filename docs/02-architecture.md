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
  transaction — enforced at runtime (`ColdStore.IsScopeActive`, checked explicitly
  in generated `Apply()` code), not compile time; full compile-time enforcement
  is a deferred, not-yet-built stretch goal (see Transactions below).
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
the generated `Ops` class and the storage/index fields it drives carry no
concurrency primitives of their own.

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
through the async Inter-Database Communication (IDC) channel below, never a
shared lock. One
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

**Status: Milestones 1-4 of the table generator are real, secondary indexes
work on both table kinds, and `PersistentTable<TKey,TRow>` is gone from the
codebase entirely.** `Instant`-kind tables, secondary indexes (all 4
`IndexKind` × `Uniqueness` combinations) on both `Instant`- and
`Persistent`-kind tables, real pre-apply cross-table validation,
`Persistent`-kind tables with `.Storage` (Load/Evict/Peek, both secondary-
index-aware). This section describes the target shape; where something is
still a stub, it says so explicitly.

**Superseded 2026-09-18: the read-your-own-writes overlay (Milestone 4, and
the equivalent scan inside the primary-key accessor) was removed entirely.**
`Get`/`Find`/every `Idx.{Index}.Find`/`Iter`/`Except`/`Range` now read
committed storage/indexes only — nothing scans uncommitted `changes` — "it
will be the user's duty to handle it," to avoid paying that scan on every
read. A staged `Insert`/`Update`/`Delete` is invisible to reads until
`Apply()` actually runs, from a later operation. Every mention of the overlay
below (the Milestone 4 status line, "Reads stay direct, and see the same
operation's own not-yet-applied writes," the "Real as of Milestone 4"
paragraph) describes the design as it stood before this date — kept as
history of how Milestone 4 worked, not current behavior. Secondary-index
accessors also changed shape entirely in the same change: no more flat
`Ops.{AccessorName}(...)` methods — see the `[Index]`'s `Accessor`/`Order`
paragraph below, which has its own inline correction.

**`Persistent`-kind tables own their physical storage directly now, just
like `Instant`-kind ones.** This used to be the one real asymmetry between
the two kinds: `Instant`-kind tables owned a `DenseArray<TRow>` and a
concrete index field directly, while `Persistent`-kind ones drove a
hand-written `PersistentTable<TKey,TRow>` composing `ColdStore`/`ColdTable` —
because those cold-storage primitives were `internal` to `RhinoDB.Lib` and
generated code, living in the consuming assembly, couldn't reach them.
That's no longer true: `ColdStore` now exposes a narrow public surface
purpose-built for generated code to drive directly —
`OpenTable<TKey,TRow>(name)`, `Stage`/`Peek<TKey,TRow>(ColdTable<TKey,TRow>,
...)`, the explicit startup-recovery entry point `CompleteRecovery()`, and a
public `IsScopeActive` getter (superseded 2026-09-14: `Put`/`Get`/`Delete`
retired along with the synchronous libmdbx write path when the WAL replaced
it — writes now go through `Stage` + the WAL's group-commit machinery, not a
direct mdbx call; see `Docs/05-wal-design.md`) — and
`ColdTable<TKey,TRow>` itself is a public opaque handle type. Notably,
`RhinoDB.Native.Transaction` and `ColdStore`'s own txn-lifecycle machinery
(`EnsureWriteTxn`/`BeginScope`/`EndScope`) stay entirely `internal` —
generated code never manages a write transaction's lifecycle itself, only
checks `IsScopeActive` before writing (the same guard `PersistentTable` used
to enforce internally, now an explicit check at the top of each `Apply()`
case). A `Persistent`-kind `Ops` class's fields/constructor/`Get`/secondary-
index accessors are now **identical in shape** to an `Instant`-kind one
(same `storage`/`primaryIndex` fields, same memory-only read logic — see
decision 1 below) — `isPersistent` only changes two things: two extra fields
(`coldTable`, `cold`) and their constructor wiring, and `Apply()`'s
Insert/Update/Delete cases doing one extra `cold.Stage(...)` call (superseded
2026-09-14: was a direct synchronous `cold.Put`/`cold.Delete` before the WAL
replaced libmdbx as the write-durability mechanism — `Stage` only appends to
an in-memory per-operation list, the actual write happens later through the
WAL's group-commit; see `Docs/05-wal-design.md`) after the same in-memory
mutation `Instant` kind does. A cold-write failure
does **not** roll back the already-applied in-memory mutation — a
pre-existing, documented limitation carried over unchanged from
`PersistentTable`, not introduced by this retirement. The generated `{Db}`
class still needs a `ColdStore` to open each table's `ColdTable` handle — a
`[Database]` class with at least one `Persistent`-kind table gets a
generated `public {Db}(ColdStore cold) : base(cold)` constructor (and
*only* that constructor — no parameterless one), opening each `ColdTable`
in the constructor body via `cold.OpenTable<TKey,TRow>(accessor)` rather
than a field initializer (field initializers can't reference even an
inherited instance property like `Cold`, the same `CS0236` category hit
during the interface retirement above — the constructor's own `cold`
parameter is used directly instead, and stored in a shared `cold` field
every `Persistent`-kind `Ops` instance is constructed with).

`PersistentTable<TKey,TRow>` played exactly the role `Table<TKey,TRow>`
used to play for `Instant`-kind tables before *its* retirement — a
hand-written, interface/delegate-driven engine the generator constructed
and drove, kept alive only by an accessibility barrier codegen couldn't
cross. Once that barrier was removed, it was retired the same way, on your
explicit direction: *"now let us go toward removing the Persistent table
and put the logic into the generator totally."* Its own hand-written test
suites (`Cold/PersistentTableTests.cs`, `Cold/LoadEvictPeekTests.cs`) are
gone too — their still-valuable scenarios (decisions 1/3/4 proofs, delete/
insert durability round trips, async-sync behavior, primary-key immutability)
were ported to `RhinoDB.Generators.Test/PersistentDurabilityTests.cs`,
proving the same guarantees hold through the generated path with nothing
underneath. Porting that coverage surfaced a real, pre-existing gap
independent of the retirement itself: `Validate()` never checked that an
`Update`'s target key actually exists (for either table kind) — an `Update`
of a genuinely nonexistent key passed `Validate()` and then silently no-op'd
inside `Apply()` (whose own per-case error handling doesn't propagate to the
caller, by design — `Apply()` is only ever meant to run after `Validate()`
has already confirmed nothing can fail), reporting `Result.Ok()` for an
update that never happened. Fixed by adding the same batch-local-then-
committed existence scan `Insert`'s duplicate check already used, inverted.

**`.Storage`'s `Load`/`Evict` read/write `storage`/`primaryIndex`/every
secondary index field directly now, the same way `Apply()`'s `Delete` case
already does its own swap-remove handling** — there's no engine class left
to delegate to. `StorageAccessor` holds a reference to the enclosing `Ops`
instance and routes `Load`/`Evict` through two private `Ops` methods:
- **`LoadInternal`** checks `primaryIndex.GetOffset(id)` first — idempotent,
  so an already-loaded row is a no-op that touches neither cold storage nor
  any index a second time (inserting into a `HashIndex`-backed unique index
  twice for the same row would be a spurious duplicate). Only on a genuine
  cold read does it insert the row into `storage`/`primaryIndex`/every
  secondary index at the newly assigned offset.
- **`EvictInternal`** removes the evicted row's secondary-index entries and,
  if `DenseArray.Delete` relocated the physically-last row into the freed
  slot, repoints that row's entries too — all without touching cold storage,
  since `Evict` never does (the durable copy stays; see Cold storage
  decision 2 below).

Without this Load/Evict index-maintenance, a `Load`/`Evict` call would
silently desync a table's secondary indexes from its real memory contents —
`Load`'d rows would be invisible to `By{Field}` accessors, and evicted rows
(or ones relocated by an eviction's swap-remove) would leave stale or
misdirected entries behind. Caught and fixed before this ever shipped, not
found in production: writing the first Persistent-kind index test surfaced
it immediately, well before `PersistentTable` itself was retired.

**`.Storage` groups `Persistent`-kind-only `Load`/`Evict`/`Peek`.** These
never touch the staged-change log, so they need no staging or validation of
their own — `tx.Accounts.Storage.Load(id)`, not staged through `Insert`/
`Update`/`Delete`.

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

**Reads stay direct, and — as of 2026-09-18 — never see the same operation's
own not-yet-applied writes.** An `Ops.Get`/`Find`/`Idx.{Index}.Find` goes
straight to real storage (`primaryIndex`/`storage`, direct fields either way
now) — no scan of staged `changes` at all. A row staged this operation (via
`Insert`/`Update`/`Delete`) is invisible to reads until `Apply()` actually
runs, from a later operation; the caller is responsible for tracking its own
staged-but-unapplied state if it needs to. (Before this date, `Ops.Get`
scanned its own staged changes most-recent-first as a read-your-own-writes
overlay — removed to avoid paying that scan on every read; see the
Transactions section's superseded-overlay note above.) A staged `Change`'s
key still has to be final the moment it's staged, not resolved later at apply
time (see `AutoIncrement` below) — `Apply()`'s in-order replay is what
resolves two zero-keyed inserts in one operation to distinct values, not an
overlay.

**Two-phase apply gives free atomicity, no rollback machinery.** `Transaction
.Apply()` checks every dirty table's `Validate()` first — nothing mutates
during this pass — before calling any table's `Apply()`. A validation
failure on the *second* table therefore never leaves the *first* table's
already-staged insert applied, which fixes, for free, the multi-table
partial-apply limitation flagged and deferred earlier in this doc's original
`PersistentTable` design. **Real as of Milestone 2**: for each staged
`Insert`, `Validate()` checks for a duplicate key and any unique-secondary-
index conflict; for each staged `Update`, primary-key immutability and the
same unique-secondary-index check, self-offset-aware (an update that keeps
its own already-owned unique value is not a false conflict against itself).
`Delete` needs no check — its target's existence was already resolved
against the overlay at stage time. The duplicate-key check is itself
overlay-aware across the *current batch*: an `Insert` scans backward through
this operation's own earlier staged changes for the same key before falling
back to committed storage, so a same-batch `Delete`-then-re-`Insert` of one
key isn't wrongly flagged, and — the concrete bug this closes — a same-batch
double-`Insert` of one key *is* now caught here instead of silently
corrupting at `Apply()` time (previously: the second physical `table.Insert`
would fail during replay, but nothing stopped the loop or surfaced that
failure to the caller, who saw an overall `Result.Ok()` regardless). One
gap, deliberately not solved here: an `Update` whose target doesn't yet
exist in committed storage (because it was inserted earlier in this *same*
batch, not yet applied) skips its own pre-check and falls through to
`Apply()`'s in-order replay to resolve correctly — the same way this already
worked before Milestone 2, since two-phase pre-validation doesn't change
apply order. **Real as of Milestone 4**: `Ops`'s secondary-index read
accessors are now overlay-aware too, the harder half `ChangeSet` v1
deliberately deferred. The check is gated on `Dirty` — the common case (no
staged writes yet on this table) costs nothing extra, same fast path as
before this milestone. When dirty, a backward scan (mirroring `Validate()`'s
duplicate-key check) finds each touched key's *final* staged state in this
batch — the last entry for that key, walking forward from it to confirm no
later entry supersedes it — and returns an overlay hit immediately if that
final row's indexed field matches. If nothing in the batch matches, the real
index/storage is consulted, but a real hit is trusted only if its primary
key was never touched by this batch at all — otherwise the real entry is
known-stale (the batch already resolved that key to something else, or
deleted it) and reporting it would show the caller data this operation
itself is about to overwrite or remove. No new allocation: the scan is
`Span`-based over the existing `changes` list, same as the rest of this
section.

**Superseded 2026-09-18: this whole overlay-aware secondary-index read
mechanism was removed** (kept above as history of what Milestone 4 built, not
current behavior — see the Transactions section's status note). Secondary
indexes also changed access shape entirely in the same change: no more flat
`Ops.{AccessorName}(...)` methods returning `Result<TRow>`/`List<TRow>` —
every table's `Ops` now exposes one `Idx` property (`{Db}{Table}IndexCollection`)
with one property per index (`Idx.{AccessorName}`, a generated
`{Db}{Table}{AccessorName}IndexOps`), each with `Find`/`Iter`/`Except` (and,
for `BTree`-kind indexes, `Range`/`Gt`/`Gte`/`Lt`/`Lte`) returning pooled,
ref-struct `QueryResultSet<TRow,TMutator>` (NonUnique) or
`QuerySingle<TRow,TMutator>` (Unique) from `RhinoDB.Lib.Tables` — `.Get()` to
materialize, `.Count` directly on the Set without materializing, `.Update`/
`.Delete` to mutate the matched row(s) without a separate `Find`-then-stage
round trip. `TMutator` is a small per-table generated struct implementing
`IRowMutator<TRow>` (`Update`/`Delete`/`WithSamePrimaryKey`), used as a
`where TMutator : struct, IRowMutator<TRow>` generic constraint rather than a
delegate field — the JIT devirtualizes the calls per closed generic
instantiation, so wiring a query result to a table's mutation methods costs
no heap allocation.

**`Table<TKey,TRow>` — the generic, interface/delegate-driven engine both
hand-written code and codegen used to share — is gone from the codebase
entirely, not just unused by codegen.** It, `ISecondaryIndex<TRow>`,
`UniqueSecondaryIndex`/`NonUniqueSecondaryIndex`, `Table.Register`, and
`INonUniqueHash<TKey>` (found to have zero real remaining usages once
secondary indexes moved off it — nothing anywhere stored a variable typed
as it) were all the right shape to hand-prove the storage engine and
secondary indexes worked at all (Stage 3, then Milestone 2's first pass) —
once proven, deleted outright, the same "hand-prove then delete the
scaffold" discipline already applied to `PlayerTable`, `InstantTable`, and
`ChangeSet<TKey,TRow>`. The generator always knows every table's exact
concrete primary-index type (`HashIndex<TKey>`/`OrderedIndex<TKey>`, from
`[PrimaryKey(kind)]`) and every secondary index's concrete type
(`HashIndex<T>`/`OrderedIndex<T>`/`NonUniqueHashSetIndex<T>`/
`NonUniqueOrderedIndex<T>`) at generation time, so the generated `{Db}`
class owns a `DenseArray<TRow>` and a concrete primary-index field directly,
and the generated `Ops` class drives all of it itself — `Insert`/`Update`/
`Delete`, including swap-remove repointing when `DenseArray.Delete`
relocates the physically-last row to fill a gap — with no interface
dispatch and no `Func<TRow,TKey> selector` delegate call anywhere; every
key read is a literal `row.{PrimaryKeyName}` field access, known at
generation time. At this point in the timeline, `PersistentTable<TKey,TRow>`
had absorbed `Table`'s old logic directly (merged, not composed) rather than
being retired the same way, since Milestone 3 hadn't wired persistent-kind
codegen up yet to inline into — it was eventually retired too, once that
barrier was removed (see above). `IUniqueIndex<TKey>` and the concrete raw
index types were untouched by any of this — at the time `IUniqueIndex<TKey>` was
still genuinely load-bearing on the primary-key slot (a runtime choice between
`HashIndex`/`OrderedIndex`, same reason `Table` used to need it), and the
raw index types are still constructed directly by both codegen and plenty
of hand-written tests. **`IUniqueIndex<TKey>` itself removed 2026-09-17** — with
`Table` retired it had no dispatch left to do: the generator owns concrete index
types directly, the ordered indexes expose their range surface through the shared
`OrderedIndex<TKey>` base (`Range`/`Gt`/`Gte`/`Lt`/`Lte`/`Iter`, all one-liners
over a single `Scan(IndexBound<TKey>, IndexBound<TKey>)`), and `HashIndex` keeps
only `GetOffset`/`Insert`/`Delete` — its `Range` was a never-called
`NotSupportedException`.

**`[Index]`'s `Accessor` and `Order` parameters, and composite indexes.**
The generated `Idx` property is named for the field itself by default
(`[Index(IndexKind.Hash, Uniqueness.Unique)] string ShortCode` → accessed as
`Idx.ShortCode.Find(string value)` — no `By` prefix; superseded 2026-09-18,
was a flat `ShortCode(string value)` method directly on the `Ops` class
before the `Idx`-based redesign above). Setting `Accessor` explicitly
overrides that name — and setting the
*same* `Accessor` value on 2-3 fields builds one **composite** index over
all of them, keyed by a named `ValueTuple` (the same composite-key mechanism
`CompositeKeyTests.cs` already proved for hand-written indexes). Field order
within a composite key defaults to declaration order; `Order` (an `int`,
default `-1` meaning "unset" — `int?` isn't a legal attribute parameter
type) overrides that per field. The generated accessor's parameter list
follows the same resolved order, so a reordering `Order` is directly
observable at the call site, not just internally.

`Accessor` also exists on `[PrimaryKey]` (renames the generated read method,
default `Get`) and `[Table]` (renames the generated property exposing that
table's `Ops` on the database's `Transaction`, default `{RowTypeName}s`) —
not on `[Database]`: unlike a row field or a table, a `[Database]` class has
no *other* generated symbol that refers to it by a name distinct from the
class's own — everything (`{Db}Transaction`, `[Table(kind, typeof(WidgetDb))]`)
already derives from the class name the author already fully controls, so
there's nothing for an `Accessor` to rename.

**`[Table].ChunkSize`** (default `4096`) sets the generated `DenseArray<TRow>`
storage field's chunk size — a bigger, densely-populated table can raise it
to grow in larger strides (fewer chunk allocations); a small or
rarely-populated one can lower it to avoid over-allocating up front.
`DenseArray` itself rounds whatever value is given up to the next power of
2, same as every other `chunkSize` in this codebase. A missing `ChunkSize`
in the attribute usage isn't distinguishable from an explicit `4096` at the
Roslyn `AttributeData` level (`NamedArguments` simply has no entry for it),
so the generator falls back to the same `4096` default the attribute's own
`{ get; set; } = 4096` property initializer declares — the two defaults are
kept in sync by hand since a source generator can't read a property's C#
initializer expression, only what's actually present in `NamedArguments`.

**Every rule the generator relies on is a diagnostic, not a silent
assumption or a crash.** A missing `[PrimaryKey]`, an empty `Accessor`
string, a composite index whose fields disagree on `Kind`/`Uniqueness`, one
with more than 3 fields, two fields with the same explicit `Order`, `[Index]`
on a `Persistent`-kind table's field (not yet supported — see above), or
`[AutoIncrement]` on a field whose type isn't one of the eight standard
integer types — each is a real Roslyn diagnostic (`RHINO001`-`RHINO007`) at
the offending attribute's own location, and only *that* table is skipped,
not the whole compilation.

**Dirty flags, not a registry.** Each `Ops` class tracks its own `bool
Dirty` (set the moment anything is actually staged — not on a no-op call,
e.g. deleting a key that doesn't exist), and a `Transaction`'s apply logic is
a flat, generator-emitted sequence of `if (table.Dirty) ...` checks — no
dynamic "which tables were touched this call" lookup. A table nothing was
staged against pays one boolean check and nothing else, regardless of how
many tables the database has.

**No stored key-selector delegates anywhere generated** (see above — this
used to be phrased relative to `Table<TKey,TRow>`'s own `Func<TRow,TKey>
selector`, which no longer exists at all now that codegen doesn't compose
`Table` anymore). Every generated `Ops.Insert` reads the row's primary-key
field directly (`row.Id`), known statically at compile time — a source
generator, unlike a hand-written generic helper, has no runtime type it
doesn't already know at the point it emits code.

**`AutoIncrement`** — an independent attribute (`[AutoIncrement]`), usually
paired with `[PrimaryKey]` but not required to be — a table can declare it on
any number of fields, guarded by the primary key, a unique or non-unique
`[Index]`, or no index at all; each tagged field gets its own independent
`AutoIncrementCounter`. On `Insert`, a tagged field's value is replaced with
the next value from its counter when it's at-or-below the "unset" threshold:
`<= 0` for a signed field (negative or zero both mean "generate one" — useful
for callers that use small negative sentinels), `== 0` for an unsigned field
(negative is impossible, so only zero means that). The field's type must be
one of the eight standard integer types (`sbyte`/`byte`/`short`/`ushort`/
`int`/`uint`/`long`/`ulong`) — anything else (a `string`, `decimal`, `bool`,
etc.) is a compile-time error (`RHINO007`), not a silent no-op. Assignment
happens via a record-struct `with` expression (every generated table already
assumes `readonly partial record struct`, so this is free), *before*
staging. Assignment happens at stage time, not apply time, for the same
overlay reason above: deferring it would mean two same-operation zero-keyed
inserts both read as key `0` until apply, colliding in the read-your-own-writes
scan. Each counter lives on the generated database class next to the
table's storage fields, *not* on the per-operation `Ops` instance — it has to
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
offsets directly. For a `[Table]`-generated table, the generated `{Db}` class
owns one `DenseArray<TRow>` field and one concrete primary-index field
(`HashIndex<TKey>`/`OrderedIndex<TKey>`, chosen by `[PrimaryKey(kind)]`)
directly — no separate coordinator type in between (see § Transactions above
for why: the generic `Table<TKey,TRow>` that used to fill that role was
retired from the codebase once codegen stopped needing its interface/delegate
indirection). The generated `Ops` class drives storage and every index
(primary and secondary) itself, unrolled per index — a delete's swap-remove
repoints *every* index on the table, not just the primary. `Update` only
re-registers an index whose own field(s) actually changed (`oldRow`'s key
expression compared against the new row's via `.Equals()`, skipping the
Delete+Insert pair when equal — added 2026-09-12; see § Transactions'
Apply() note for the historical bug this has to avoid). A found entry in a
unique index during validation is only a real conflict if it belongs to a
different offset than the row's own (the self-offset-aware check, unrelated
to and unaffected by this apply-time optimization).
Hand-written, non-generated code that still wants a general-purpose
multi-index table coordinator (there is currently no such use case in the
codebase — both table kinds now inline their own storage + primary-index
slot directly in generated code, the same shape `Table`/`PersistentTable`
used to have as standalone classes) can still compose `DenseArray<TRow>`
plus the raw index types by hand; nothing about their removal from the
generated path prevents that.

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

**A file's own fsync doesn't cover the directory entry that makes it findable
— `ColdStore.Open` closes that gap for its own files, once, on first creation.**
On POSIX filesystems, fsyncing a file only guarantees *that file's contents*
are durable, not the parent directory's record that the file exists — a crash
right after creating a brand-new file can leave its data physically on disk but
practically unreachable (no directory entry pointing to it), even though the
file itself was fsynced. This is a well-documented, historically recurring
class of database bug (SQLite, PostgreSQL, and other real-world databases
have all hit it — this project's own 2026-09 investigation was prompted by a
LinkedIn post describing the same class of bug in a WAL-segment-rotating
database design). Checked directly against RhinoDB's own vendored libmdbx
source (the whole ~1.7MB tree, not just its docs) for this specific
mechanism: no `O_DIRECTORY`, no directory-fd `fsync`, no evidence libmdbx
does this itself. **The exposure is architecturally much narrower here than
in a segment-rotating WAL design**, though: libmdbx creates its `mdbx.dat`/`mdbx.lck` files
exactly once, the first time `ColdStore.Open` runs against a directory that
doesn't have them yet — every `Insert`/`Update`/`Delete`/`Confirmed` commit
after that writes into the *same*, already-existing file via copy-on-write,
never creating a new file. So the gap can only bite at one specific moment (a
crash between a fresh database's very first launch and whenever the
filesystem's own journaling happens to persist that directory entry on its
own), not on every write.

Fixed anyway, since the one-time cost of closing it is small: `ColdStore.Open`
checks whether its target directory had any files in it *before* opening
(`isFreshDirectory`), and if so, calls `RhinoDB.Native.DirectorySync.TrySync`
(a raw POSIX `open`/`fsync`/`close` on the directory itself, added specifically
for this — libmdbx exposes nothing for it) immediately after `mdbx_env_open`
succeeds. Failure surfaces as a real `Result.Error` (`DbError
.ColdStorageDirectorySyncFailed()`), not a silently-ignored best-effort attempt
— if we can't confirm the newly-created files are durably findable, that's
worth knowing at startup, not discovering after a crash months later. A no-op,
always-success on Windows: NTFS's crash-consistency model is journaled
differently, and this specific idiom doesn't have a Windows equivalent the way
it does on ext4/XFS — matches this doc's stated production target (Linux
servers). Untested on Linux from this Windows dev environment (same caveat
already carried for the rest of the native binding — see the plan's Part A
verification table); the mechanism (plain `open`+`fsync`+`close`) is standard
and well-understood, but not exercised at runtime here.

`Load`/`Evict`/`Peek` are distinct from `Insert`/`Delete` — they move data between
memory and libmdbx without changing the durable data itself, so they produce no
change-propagation entry. `Insert`/`Delete` write through to libmdbx because the
row's actual state changed; `Load`/`Evict` touch memory only, and `Peek` is a
read-only point lookup straight into libmdbx that returns a row's current value
*without* loading it into memory — so a broad query (a leaderboard scan over
offline accounts) doesn't force every row it touches into the working set just to
read it. Recovery starts empty except for every non-`Evictable` table, which the
generated `{Db}Loader` brings back in full before anything else runs (see
Restart & recovery below) — `Evictable` tables get no automatic warm-up at all.

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

Closing that gap needed a genuinely new decision, not implied by anything
already built: a **bounded commit-coalescing window** — after a commit
succeeds, wait a small, configurable delay before actually firing
`mdbx_env_sync_ex`, giving other `Confirmed` calls that arrive in that window
a chance to pile onto the same pending sync and share its cost — the same
idea as Postgres's `commit_delay`/`commit_siblings`. This is deliberately two
separate, orthogonal knobs, not one: **txn granularity** (one write txn per
`Run` call, decided, staying that way — bundling unrelated operations into
one txn would couple their atomicity, so a later operation's failure could
roll back an earlier, already-succeeded one nobody asked to link) versus
**sync granularity** (opportunistic-only by default; the coalescing window is
the deliberate lever on top of it). The mechanism is isolated to one place,
`ColdStore.EndScope`'s sync-firing branch — the delay touches nothing in
`DbExecutionLoop` or any generated `Insert`/`Update`/`Delete`/`Load`/`Evict`/
`Peek` code, the same containment that already let the original async-sync
addendum land without perturbing anything built before it.

**Built and benchmarked 2026-09-13** — `ColdStore.Open`'s new
`commitCoalescingWindow` parameter (default `TimeSpan.Zero`, preserving the
exact prior always-fire-immediately behavior for every existing caller).
Correctness proven directly (`ColdStoreTests.cs`'s
`EndScope_WithCoalescingWindow_*` tests: two `Confirmed` commits arriving
within the window return the literal same `Task<int>`, one arriving after it
closes gets its own). **Whether it *helps* turned out to depend entirely on
which cost dominates, and on this platform, at the originally-suggested
0-2ms scale, it doesn't help at all — not because the mechanism is wrong, but
because of a Windows platform floor**: `RhinoDB.Sandbox.Benchmark`'s new
`ConfirmedCoalescingBenchmarks` fired 16 concurrent `Confirmed` writes (the
only pattern coalescing can affect — a purely serial caller never has a
second commit to piggyback with) two ways. Against fresh, never-before-
written keys, the batch was dominated by the ~900μs-1ms fresh-page-write
cost from the entry above `Commit()` pays regardless of Confirmed/Optimistic
or any sync scheduling — no coalescing window can touch a cost that happens
before it even runs, and none of window=0/1/2/5ms showed a meaningful
difference (~15ms total either way). Against the *same*, already-settled key
(isolating fsync cost from that confound: each `Commit()` here costs only
~50-130μs), window=1/2/5ms all landed at the exact same ~15.5ms regardless
of value — a dead giveaway, confirmed by directly probing `Task.Delay(1..5)`
on this machine (measured 9-15ms actual for all of them), that plain
`Task.Delay` cannot reliably wait for less than Windows' default ~15.6ms
system timer tick. The mechanism itself was confirmed correct despite this —
testing at window=20ms (above the tick) landed at ~31ms (≈2 ticks), not the
~1.6ms 16 independently-firing commits would cost, proving the batch really
did share one physical `mdbx_env_sync_ex` call. **Conclusion: leave the
default at `TimeSpan.Zero` on Windows** — a ~15.6ms granularity floor is 15-
300x larger than the microsecond-scale cost it exists to amortize, so
enabling it here is a pure net loss until a higher-resolution wait is built
(e.g. `timeBeginPeriod(1)` or a spin-wait for sub-tick windows, neither
attempted — out of scope for what this round of benchmarking was answering).
Untested on Linux, whose kernel timers are generally much finer-grained —
worth a dedicated look there specifically if sustained concurrent-`Confirmed`
throughput ever becomes a real bottleneck in production, not before. Full
writeup: `Docs/03-roadmap.md`'s 2026-09-13 entry,
`Docs/Dev/RhinoDB.Lib/Cold/ColdStore.md`, and
`Docs/Dev/RhinoDB.Sandbox.Benchmark/Benchmarks/ConfirmedCoalescingBenchmarks.md`.

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
simply discards it, and a fresh process starts every table completely empty,
the same as a brand-new database would.

**Built 2026-09-12: `[Table].Evictable` (default `false`) plus a generated
`{Db}Loader` class replace the old speculative "`OnStart` hook" sketch with
a real, concrete mechanism.** A `Persistent`-kind table with `Evictable =
false` doesn't even generate `.Storage` (Load/Evict/Peek) — the *only* way
such a table could ever hold any data is a full eager load, so the generator
also emits `{Db}Loader.LoadAsync({Db} db)`, `public virtual`, whose default
implementation calls a new internal `BulkLoadFromCold()` on every non-
evictable table's `Ops` — a full cursor scan of that table's cold
sub-database (`ColdStore.ScanAll`, backed by real libmdbx cursor bindings)
feeding every row straight into `storage`/`primaryIndex`/every secondary
index, skipping `Validate()`'s duplicate-key/uniqueness checks entirely
(each row already passed them the first time it was ever written, so
re-checking would just repeat proven-safe work) and skipping the staged-
change log too (there's no operation to roll back, no cold write-through to
redo — the data is already durable, that's the whole reason it's being
loaded). Per-table loads run concurrently (`Task.Run` per table inside
`LoadAsync`) since libmdbx is multi-reader — separate `ScanAll` calls never
contend with each other, and different tables' storage/index fields are
always disjoint, so there's no shared state between them either. A consumer
awaits `loader.LoadAsync(db)` once at startup, before accepting traffic —
reusing no special engine hook, just an ordinary async call the hosting code
sequences itself.

**`Evictable = true` opts a table *out* of that automatic full load and
exposes `.Storage` instead**, so the consumer manages residency by hand
(e.g. loading specific rows on demand inside RPC handlers) — the design this
doc originally sketched as the default for every table. A subclass
overriding `LoadAsync` can still call the base implementation and then reach
into an `Evictable` table's own `BulkLoadFromCold()` directly (via its
`Ops`, obtained through a same-assembly-only `CreateLoaderTransaction()`
wrapper) if it wants to eager-load that one too — the choice is explicit and
per-table, not baked into the engine's defaults.

**Restart is the routine, whole-table version of the eviction hazard already
documented above, not a new one.** A unique secondary index only enforces
against currently-loaded rows; after *any* restart, a table left partially
loaded (an `Evictable` one the consumer didn't fully reload) is in exactly
the state a deliberately-`Evict`ed row is in — unenforced, until reloaded.
This is exactly why `Evictable = false` is the *default*: a table that never
opts into partial residency can never be caught half-loaded after a restart,
since the loader always brings it back in full before anything else runs.
The hazard only exists at all for a table that explicitly chose `Evictable =
true` and the consumer's own residency management leaves gaps — a stated
trade-off of that choice, not a gap in the engine.

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

**Built 2026-09-18: opt-in genesis replay, alongside (not replacing) the
"reopen the environment" recovery model above.** That opening claim — no
second recovery mechanism, a fresh process starts every table empty and
reloads from libmdbx — still describes the *default* startup path
unchanged. Genesis replay is a deliberately separate, explicitly-requested
alternative: reconstruct a table's full history from the very first
operation, in memory, for debugging/inspection, never touching cold storage
or the live WAL. Two things had to exist first: `WriteAheadLog.Truncate()`
(see `Docs/05-wal-design.md` Phase 3) destroys everything it wipes at every
checkpoint, so *some* form of archive-before-truncate is required just to
have history older than "since the last checkpoint" to replay at all; and a
way to replay a decoded change back into memory without the normal
`Apply()` path's `cold.Stage(...)` call, since that call is exactly what
must never happen during replay.

- **`WalArchive` (`RhinoDB.Lib.Durability`) is the always-on half** — not
  gated behind anything, since there's no way to opt into history later that
  wasn't preserved at the time. `CheckpointEngine.RunCheckpoint` now takes
  the full pre-collapse `DecodedWalEntry[]` it's about to fold into libmdbx
  and, right before the existing `wal.Truncate()` call, re-encodes them
  (same `WalFileHeaderCodec`/`WalRecordCodec` framing `wal.dat` itself
  uses) into a new sequentially-numbered segment file under
  `wal-archive/`. An archive-write failure doesn't fail the checkpoint or
  block the truncate — archiving is a side-channel for replay, not part of
  what "checkpoint succeeded" means for the live game — but it's surfaced
  in the returned `Result` rather than silently swallowed. A crash between
  the libmdbx commit and the archive write produces a harmless duplicate
  segment on the next checkpoint (never a gap, since the ordering — commit,
  *then* archive, *then* truncate — never changes), which the reader below
  tolerates directly rather than the write side trying to be perfectly
  atomic about it.
- **Pruning is manual and file-granular, on purpose.** Segments are just
  files (`00000001.wal`, `00000002.wal`, ...); deleting the oldest ones by
  hand to reclaim disk reduces how far back "genesis" reaches without
  needing a dedicated API — the reader establishes its starting point from
  whichever segment is oldest still present, no special-casing required. A
  gap from deleting a segment out of the *middle* of the chain is a
  different story and is rejected outright (`ErrorKind.WalArchiveGap`), not
  silently reconstructed as a wrong intermediate state.
- **`WalArchive.ReadHistory` never opens `wal.dat` itself.** That file is
  held with `FileShare.None` for the entire lifetime of whichever
  `ColdStore` owns it, so a second reader in the same process would be a
  guaranteed sharing violation — the exact failure mode hit and fixed while
  building this. Instead, `ColdStore.PendingWalTail` exposes the tail that
  instance already decoded at `Open()` time (the same data
  `CompleteRecoveryAsync()` would otherwise fold into libmdbx), and the
  generated `LoadFromGenesis` takes the caller's already-open `ColdStore`
  rather than a raw path so it can read that instead of the file.
- **Per-table `ReplayApply` mirrors `Instant`-kind `Apply()`'s Insert/
  Update/Delete bookkeeping exactly, minus `cold.Stage(...)`, one change at
  a time rather than a staged batch** — replay is inherently step-by-step
  (that's what makes it useful for debugging "how did we get here," not
  just a slower way to reach today's state), so there's no batching to
  mirror from the `Persistent`-kind side either. The generated
  `{Db}Loader.LoadFromGenesis(db, cold, upToLsn:, onEntryApplied:)` is the
  explicit opt-in — a distinct method rather than a flag on `LoadAsync`,
  much harder to reach by accident — that walks `WalArchive.ReadHistory`'s
  merged, gap-checked entries in order, optionally stopping at a given LSN
  or invoking a callback per entry so a caller can freeze/inspect the
  reconstructed state at any point in history, not only the end.

## Inter-Database Communication (IDC) (designed, not yet built)

**Renamed 2026-09-13 from "Change propagation"** — that name conflated two
separate concerns: actor-to-actor delivery (this section, database/shard to
database/shard) and server-to-client delivery ([Roadmap Stage
8](03-roadmap.md#stage-8--viewtable-only-client-access-subscribe-and-diff)).
They share the same underlying change-emission mechanism described below but
are independent pipelines with independent wire-format needs — see "Three
serialization pipelines" further down. `PropagationMode`
(`Optimistic`/`Confirmed`, the per-`Run`-call durability choice) is unrelated
and keeps its name.

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

For an instant-only change, or an `Optimistic` transaction, IDC delivery is
immediate — enqueued right after the in-memory mutation, not waiting on a
libmdbx write. For a `Confirmed` transaction, delivery waits until *after*
its commit actually succeeds — otherwise a peer could be told about a
change that then fails to durably land, exactly what `Confirmed` exists to
prevent. This costs nothing extra: `Confirmed` already blocks the single-writer
loop on that same await, so nothing else can interleave regardless of when
delivery happens within it.

### Three serialization pipelines, not one

- **Cold storage** — plain MemoryPack, fixed, never configurable. Unrelated to
  IDC; mentioned here only to draw the boundary.
- **IDC (this section)** — `[Database].Idc` declares `WireFormat.MemoryPack`
  (default) or `VersionedMemoryPack`. Actor-to-actor only: bytes never leave
  the set of `DbContext`s an operator controls, so a RhinoDB-specific encoding
  is fine.
- **Client access** (Stage 8) — its own, separate wire-format declaration
  (`VersionedMemoryPack`, or `MessagePack` for non-C# clients). Deliberately
  independent from IDC's choice — a database's peers and its clients can have
  entirely different version/language constraints, so coupling the two would
  force a lowest-common-denominator format on both.

### Fully generated wire (de)serialization — no hand-declared type, no third-party generator dependency

A first design pass had the table generator emit a `[MemoryPackable]`-attributed
wire struct for MemoryPack's own generator to complete. Rejected once we
confirmed this isn't merely fragile, it's a hard limitation of the incremental
generator model: a generator's `ForAttributeWithMetadataName` pipeline only
ever scans the *original* user-written syntax trees present when the driver
starts — never another generator's `AddSource` output, regardless of
registration order. Two generators can coexist in one compilation (as
`TableGenerator` and `DbErrorGenerator` already do in this repo) only by each
independently scanning hand-written attributes; neither can consume the
other's generated code.

Fix, for plain `WireFormat.MemoryPack`: the table generator emits
`Serialize`/`Deserialize` methods that call MemoryPack's own public low-level
primitives directly — `MemoryPackWriter<TBufferWriter>`/`MemoryPackReader`,
the same building blocks MemoryPack's generator itself would have called,
field by field, in declaration order (default MemoryPack is positional). No
intermediate wire *type* is generated at all, and no dependency on MemoryPack's
generator running in combination with ours — we call its runtime library
directly, the same way its own generated code would have. This is safe
specifically because IDC is RhinoDB-to-RhinoDB only: we own both ends of the
wire, so replicating a simple, stable, positional encoding ourselves carries
little drift risk (a future incompatible change to MemoryPack's low-level
writer/reader API would need a new RhinoDB version regardless of which
approach was used).

`MessagePack` (Stage 8, client-facing, genuinely external non-C# consumers) is
deliberately **not** given the same treatment. Interop correctness with real
external clients matters more there than the boilerplate savings, so that path
keeps a hand-declared, attributed DTO processed by the real MessagePack-CSharp
generator — a subtle hand-rolled encoding bug would break someone else's
client in a way that's much harder to catch than an internal IDC mismatch.
`VersionedMemoryPack` is a later increment either way — its tagged, per-field
encoding is more intricate to replicate correctly than plain MemoryPack's.

### Location-transparent transport

**Revised 2026-10-04, superseding the in-process option below**: a process now
hosts exactly one database (`RhinoHostBuilder.AddDatabase` may only be called
once — see Hosting). Multiple databases never again share a CLR/process
boundary, so there is no same-process peer for IDC to reach — every peer is
necessarily a different process (same machine or a different one). IDC must
not know or care which of those two it's talking to — one `IIdcTransport`
interface, two implementations, swappable without touching sender/receiver
logic:

```csharp
public interface IIdcTransport {
    ValueTask SendAsync(DatabaseId target, TableId table, ChangeEnvelope envelope, CancellationToken ct);
    IAsyncEnumerable<(DatabaseId Source, TableId Table, ChangeEnvelope Envelope)> Receive(CancellationToken ct);
}
public readonly record struct ChangeEnvelope(ChangeKind Kind, byte[] Key, byte[]? Row, long Sequence);
```

- `LocalIpcIdcTransport` — same machine, separate process — named pipe, Unix
  domain socket, or shared memory.
- `NetworkIdcTransport` — different machine — TCP/QUIC/gRPC.

(The `InProcessIdcTransport`/`Channel<T>`-between-two-`DbContext`s design and
its deferred same-process zero-serialization optimization, both sketched here
before 2026-10-04, are dropped along with the one-process-many-databases model
they depended on — not merely deprioritized.)

### Declarative surface

Nothing beyond these two attributes is hand-written — wire (de)serialization,
the sender hook at commit time, and the receiver's apply-through-`Run` loop are
all generated:

```csharp
[Database(Idc = WireFormat.MemoryPack)]
public partial class GameDb : DbContext<GameDbTransaction> { }

[Table(TableKind.Persistent, typeof(GameDb), IdcDelivery = DeliveryGuarantee.Reliable)]
public readonly partial record struct Club([PrimaryKey] int Id, string Name, decimal Balance);
```

Sender/receiver shape (illustrative, not yet built):

```csharp
// sender - hooked exactly where Change<TKey,TRow> is already enqueued at commit time
void OnCommitted(TableId table, Change<int, Club> change) {
    var envelope = new ChangeEnvelope(change.Kind, ClubIdc.SerializeKey(change.Key),
        change.Kind == ChangeKind.Delete ? null : ClubIdc.Serialize(change.Row), NextSequence());
    foreach (var peer in subscribers[table])
        _ = transport.SendAsync(peer, table, envelope, ct);   // per-table DeliveryGuarantee governs retry/ack
}

// receiver - identical regardless of which IIdcTransport is behind it
await foreach (var (source, table, envelope) in transport.Receive(ct)) {
    await peerDb.Run((ctx, tx) => {
        if (envelope.Kind == ChangeKind.Delete) tx.Clubs.Delete(ClubIdc.DeserializeKey(envelope.Key));
        else tx.Clubs.Update(ClubIdc.Deserialize(envelope.Row).Id, ClubIdc.Deserialize(envelope.Row));
        return Result.Ok();
    }, PropagationMode.Optimistic);   // trusted path - source already validated it once
}
```

See [Roadmap Stage 6](03-roadmap.md#stage-6--inter-database-communication-idc)
for status and sequencing against Stages 7-8.

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
