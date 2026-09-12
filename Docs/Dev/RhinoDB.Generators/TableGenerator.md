# `src/RhinoDB.Generators/TableGenerator.cs` — dev notes

Excerpts of the design-rationale comments trimmed from this file during the
2026-09-13 comment cleanup. The source itself stays comment-free; look here
for the "why" behind a symbol. Headings match the method/property/class the
comment sat above.

## File header

Generated code for EVERY table - Instant or Persistent kind - owns its
physical storage directly: a DenseArray\<TRow\> field plus a concrete
primary-index field (HashIndex\<TKey\>/OrderedIndex\<TKey\>) on the `{Db}`
class, driven directly by the generated Ops class, with no engine class in
between. This used to be true only for Instant-kind tables - Persistent-kind
ones drove a hand-written `PersistentTable<TKey,TRow>` composing
ColdStore/ColdTable, because those cold-storage primitives were `internal` to
RhinoDB.Lib and generated code (living in the consuming assembly) couldn't
reach them. That barrier is gone: ColdStore now exposes a narrow public
surface purpose-built for this - `OpenTable`, `Put`/`Get`/`Delete`/
`Peek<TKey,TRow>(ColdTable<TKey,TRow>, ...)` - and `ColdTable<TKey,TRow>`
itself is a public opaque handle type. Notably, `RhinoDB.Native.Transaction`
and ColdStore's own txn-lifecycle machinery (`EnsureWriteTxn`/`BeginScope`/
`EndScope`) stay entirely internal - generated code never manages a write
transaction's lifecycle itself, only checks `ColdStore.IsScopeActive` (now
public) before writing, exactly the guard PersistentTable used to enforce
internally. `PersistentTable<TKey,TRow>` itself is gone from the codebase
entirely - the same "hand-prove a shape, then delete the hand-written
scaffold once codegen supersedes it" discipline already applied to
`Table<TKey,TRow>`.

Practical effect: a Persistent-kind Ops class's fields/constructor/Get/
secondary-index accessors are now IDENTICAL in shape to an Instant-kind one
(same `storage`/`primaryIndex` fields, same read logic - reads still never
touch cold storage, decision 1) - `isPersistent` only changes two things: (1)
two extra fields (`coldTable`, `cold`) and their constructor wiring, and (2)
`Apply()`'s Insert/Update/Delete cases do one extra `cold.Put`/`cold.Delete`
call after the same in-memory mutation Instant kind does, guarded by an
explicit `cold.IsScopeActive` check at the top of the case - mirroring
PersistentTable's original check-before-mutating order exactly (memory still
isn't rolled back if the cold write itself fails afterward - a pre-existing,
documented limitation, not new here). `.Storage.Load/Evict/Peek`
(Persistent-kind only) read/write `storage`/`primaryIndex`/every secondary
index field directly, the same way `Apply()`'s Delete case already does its
own swap-remove handling.

`EmitOpsClass` itself is a one-line dispatch on `table.Kind` to
`EmitInstantOpsClass`/`EmitPersistentOpsClass` - each fully self-contained,
with no `isPersistent` branching inside either. Genuinely kind-independent
pieces (Get/Iter/secondary-index accessors/staging/Validate) live in small
shared helpers that take no kind parameter at all; anything that actually
differs per kind (fields/constructor/Apply()/.Storage) is written out
separately in full for each kind rather than branched inline.

Every rule this generator relies on is a diagnostic, not an assumption or a
crash: `ToTableModel` never throws on malformed input (a missing
`[PrimaryKey]`, an empty Accessor, a composite index that disagrees with
itself) - it reports a real compile error and that one table is skipped
(diagnostics non-empty => Model is null), so one broken table doesn't take
down the whole compilation and the author sees an actual message, not a
generator stack trace.

## `Initialize` — the `predicate:` on the table pipeline

`record struct Foo(...)` parses as `RecordDeclarationSyntax` (the
`RecordStructDeclaration` kind), not `StructDeclarationSyntax` - a plain
`struct Foo { }` is the only thing that IS the latter. (Historical note: an
early version of this predicate only matched `StructDeclarationSyntax` and
silently matched zero tables, since every row in this project is declared as
`record struct`.)

## `Initialize` — the diagnostics `RegisterSourceOutput`

Registered on the per-table (uncollected) pipeline, separately from the
codegen output - each table's own diagnostics are reported exactly once
regardless of how many `[Database]` classes exist (`Emit` runs once per
database, over the same collected table list, which would otherwise
re-report every diagnostic once per database).

## `ToTableModel` — `primaryCtor`

The primary (positional) constructor - not the copy constructor a record
struct also has (single parameter of the row's own type).

## `ToTableModel` — `indexedParams`

One entry per `[Index]`-attributed parameter, carrying its declaration
position (the "order fields sort by, by default" `Order` falls back to)
before any grouping happens.

## `ToTableModel` — `indexes` grouping

Grouped by Accessor: 2-3 fields sharing one Accessor form a single composite
index over them, ordered by `Order` (falling back to declaration position) -
a lone field is just a 1-field "composite". `GroupBy` preserves
first-occurrence order, so index declaration order in generated output
tracks row field declaration order.

## `PrimaryIndexType`

The concrete primary-index class for a table - `HashIndex`/`OrderedIndex`
are always Unique (a primary key can't be anything else).

## `ConcreteIndexType`

The concrete index class for a given (Kind, Uniqueness) pair - the one place
this mapping is decided, used for both the `{Db}` class's field declarations
and the Ops class's constructor parameter types.

## `KeyType`

A lone field's own type, or a named `ValueTuple` type for a composite index -
named so the tuple stays self-documenting even though generated code mostly
builds/consumes it positionally.

## `KeyExpr`

The key expression to read off a row variable for this index - a single
member access, or a positional tuple literal for a composite.

## `EmitStorageAndIndexFields`

`storage`/`primaryIndex`/autoIncrement counters/index fields - identical for
both kinds. Persistent kind appends its own `coldTable`/`cold` fields
separately (right after calling this), since those genuinely don't exist for
Instant kind.

## `AppendAutoIncrementAndIndexParams` / `AppendAutoIncrementAndIndexAssignments`

Position-matched 1:1 against `EmitDatabase`'s `CreateTransaction()` call
site, both generated from the same `TableModel.AutoIncrementFields`/`Indexes`
order - shared by both constructor emitters.

## `EmitGetMethod`

Read-your-own-writes: most-recent-first scan over this operation's own
staged changes before falling through to real storage - the same algorithm
`ChangeSetTests.cs` already proved, inlined per table. Span + `ref readonly`
avoids a struct copy per candidate examined (a Change carries a full TRow) -
see `Docs/01-performance-principles.md` §1/§2 and `Docs/02-architecture.md` §
Transactions. Named "Get" by default; `[PrimaryKey(Accessor = "...")]`
renames it. Never touches cold storage even for Persistent kind -
Get/GetByOffset are memory-only by design (see Cold storage decision 1).

## `EmitIterMethod`

Sequential enumeration of every row currently in memory, in DenseArray
offset order - real storage only, deliberately NOT overlay-aware (unlike
Get, a full scan merging in this operation's own staged-but-unapplied
changes would need to skip deleted rows and dedupe updated ones; not built
until a real need shows up, matching this project's "declared explicitly,
don't build ahead of a proven need" posture elsewhere). Lazy (`yield
return`) since a full-table walk is already the explicitly-slow escape
hatch, not a hot-path accessor.

## `EmitSecondaryIndexAccessors`

Secondary-index read accessors - overlay-aware (Milestone 4): when `Dirty`,
this operation's own staged-but-not-yet-applied changes are consulted before
falling back to the real index/storage, the same read-your-own-writes
guarantee the primary Get() accessor already has. The `Dirty` check keeps
the common case (no staged writes on this table yet) exactly as cheap as
before this milestone - the overlay scan only runs when there's actually
something to overlay.

Algorithm (backward scan, mirroring `Validate()`'s duplicate-key check): for
each staged change, walk forward from it to see if a LATER entry in the
batch already claims the same primary key - if so, this entry is stale
within the batch itself and is skipped; otherwise it's that key's final
staged state for this batch. A final state whose row's index-field value
matches the target is an overlay hit, returned immediately (it doesn't
matter what real storage/the real index currently say, this operation's own
write wins). If nothing in the batch matches, fall back to the real
index/storage - but the real hit might now be stale (its primary key was
touched by this batch and its field no longer matches, or it was deleted),
so a real hit is only trusted if its primary key was never touched by the
batch at all.

## `EmitValidateMethod`

Real pre-apply validation: nothing mutates during this pass, so a later
table's failure (see the generated `Transaction.Apply()`) never leaves this
table's already-staged changes applied - the free cross-table atomicity
Milestone 2 exists to prove. Checked per staged change, in order, directly
against each concrete index's own `GetOffset`:

- **Insert**: does the key already exist "as of just before this entry"?
  Scans backward through EARLIER entries in this same batch first (an
  intervening Delete clears it, an intervening Insert/Update means it's
  still live) - falls back to real committed storage only if no prior entry
  in this batch mentions the key at all. Without the batch-local scan, a
  same-batch "Delete then re-Insert the same key" would be wrongly flagged
  as a duplicate (the row is still physically committed until `Apply()`
  actually runs); without ALSO checking, a same-batch double-Insert of the
  same key would wrongly pass (each half individually looks fresh against
  committed storage) and silently corrupt at `Apply()` time instead of
  failing loudly here. "Committed storage" here means memory only (never
  cold), matching decision 1 - true for both kinds, since `primaryIndex` is
  a direct field either way now.
- **Update**: primary-key immutability, then existence (the same
  batch-local-then-committed scan Insert's duplicate check uses, inverted -
  an Update whose key was never inserted or committed anywhere reports
  `IndexKeyNotFound` here instead of silently no-op'ing at `Apply()` time.
  Found via a real gap: `Apply()`'s own per-case `break` on a failed offset
  lookup never propagates to the caller - `Validate()` passing is what
  makes `Apply()` a "can't fail" replay, so an unchecked
  Update-of-nonexistent-key used to slip through `Validate()` and then
  silently do nothing at `Apply()` time, reporting `Result.Ok()` for an
  update that never happened), then unique-index conflicts using the row's
  real physical offset *if it already has one* (skipped entirely whenever a
  table simply has no unique secondary index at all). A key with no offset
  yet (inserted earlier in this same batch, not yet applied - already known
  to exist via the batch-local scan above) is left to `Apply()`'s own
  in-order replay to resolve the unique-index check correctly, same as
  before this validation existed - `Apply()` replays in staged order, so by
  the time this Update replays, the earlier Insert has already physically
  landed.
- **Delete**: nothing to check - existence was already resolved against the
  overlay at stage time (see Delete in `EmitStagingMethods`).

## `EmitInstantApply`

Unconditionally inlines the same swap-remove handling `Table<TKey,TRow>`
used to (`DenseArray.Delete` can relocate the physically-last row into the
freed slot, and every index - primary and secondary - must repoint to
follow it). Update only touches an index whose own field(s) actually
changed - compares oldRow's key expression against the new row's via
`.Equals()` (a single field access, or structural `ValueTuple` equality for
a composite index) and skips the Delete+Insert pair entirely when they're
equal, since the index's existing mapping already points at the right
offset. Unlike an early, buggy attempt at this same optimization noted in
`Docs/03-roadmap.md` Stage 3 (which apparently skipped re-registration under
the wrong condition and left a stale entry behind - a real self-collision
risk: reverting a field back to a value it held earlier could then collide
with its own uncleaned stale entry), this always deletes the OLD mapping
whenever the value differs, before inserting the new one - never
conditionally skips the delete alone.

## `EmitPersistentApply`

The same inlined swap-remove handling `EmitInstantApply` uses, plus exactly
one extra step per case - a `cold.Put`/`cold.Delete` call after the same
in-memory mutation, guarded by an explicit `cold.IsScopeActive` check at the
top of the case (mirroring `PersistentTable`'s original check-before-
mutating order, back when it existed as a hand-written class - a cold-write
failure does NOT roll back the already-applied in-memory mutation, a
pre-existing, documented limitation carried over unchanged).

## `EmitBulkLoadMethods`

Bulk-load fast path: called only by the generated `{Db}Loader` (and any
hand-written extension of it) while populating memory from a cold-storage
`ScanAll` at startup. Every row it's given already lived in cold storage
under this exact key - it was validated (duplicate-key, uniqueness) the
first time it was ever written, so re-running `Validate()`'s checks here
would just repeat work already proven safe. Skips staging entirely too:
there's no operation to roll back, no read-your-own-writes overlay to keep
consistent, and no cold write-through to redo (the data is already durable -
that's the whole reason it's being loaded). Internal, not public: only
generated code in this same assembly (the `{Db}Loader`) is expected to call
it.

`BulkLoadFromCold` itself: called once per table by the generated
`{Db}Loader` - does the whole cursor scan + bulk load in one call so
`coldTable`/`cold` (both private) never need to be exposed outside this
class; the loader only ever needs "load everything," never row-by-row
control from outside.

## `EmitStorageAccessor`

`.Storage` groups the Persistent-kind-only Load/Evict/Peek accessors - these
read/write `storage`/`primaryIndex`/every secondary index field directly (no
engine class in between anymore), never the change log, so they need no
staging/validation of their own. Peek is a pure pass-through (it never
mutates memory - see the Peek-bypasses-Run design note in
`Docs/02-architecture.md` § Cold storage) - Load/Evict route through private
Ops methods since they mutate storage AND every secondary index field. Only
called when `Evictable = true` - a non-evictable table is eager-loaded in
full by the generated `{Db}Loader` and never partially evicted, so there's
nothing for `.Storage` to do; not generating it at all means a consumer
literally cannot call Load/Evict/Peek on such a table, a compile-time
guarantee rather than a runtime one.

`LoadInternal`: idempotent - a row already in memory needs no cold round
trip at all - checked first so this never touches cold storage (or requires
an active scope) for a no-op call, and so a repeat Load never
double-inserts into a secondary index.

`EvictInternal`: removes the row's own secondary-index entries and, if
`DenseArray.Delete` relocated another row to fill the freed slot, repoints
that row's entries too - the same swap-remove handling `Apply()`'s Delete
case does, just without any cold-storage interaction (Evict never touches
cold storage - the durable copy stays, see Cold storage decision 2).
Idempotent: a row not currently in memory is treated as already evicted.

## `EmitDatabase` — Instant-kind field loop

Instant-kind fields are self-contained `new ConcreteType()` initializers
with no reference to any sibling field.

## `EmitDatabase` — Persistent-kind field loop

Persistent-kind fields are the SAME `DenseArray`/primary-index shape
Instant-kind has, plus a `ColdTable` field. Unlike `storage`/`primaryIndex`,
`ColdTable` can't be a field initializer's `new(...)` - opening it needs a
real `ColdStore`, only available once the base `DbContext<TTx>(ColdStore)`
constructor has already run (a field initializer can't reference even an
inherited instance property - the same CS0236 category hit during Milestone
2's interface retirement) - so it's assigned in the constructor body
instead, using the constructor's own `cold` parameter directly (never
`this.Cold`, sidestepping the question of exactly when that inherited
property becomes safe to read). The shared `cold` field itself (one per
database, not one per table) is what every Persistent-kind Ops instance
uses for its own `IsScopeActive` check / Put/Get/Delete/Peek calls.

## `EmitDatabase` — `CreateLoaderTransaction()`

Internal-only wrapper so the generated `{Db}Loader` (a separate, unrelated
class - not a subclass of `{Db}`, so it can't reach a `protected` member)
can still get a real Transaction to bulk-load through. `CreateTransaction()`
itself has to stay `protected` (the override on `protected internal
virtual` narrows across the RhinoDB.Lib/consumer-assembly boundary), so this
is the only way to reach it from same-assembly, non-derived generated code.

## `EmitLoader`

`{Db}Loader.LoadAsync` eager-loads every non-Evictable Persistent-kind
table's entire cold-storage contents at startup - the only way such a table
could ever have any data in memory at all, since it doesn't expose
`.Storage` to load rows on demand. Evictable tables are left alone by
default (the consumer manages their residency by hand), but a subclass
overriding `LoadAsync` can still call `BulkLoadFromCold()` on one directly
if it wants to eager-load it too. Per-table loads run concurrently
(`Task.Run` per table) - libmdbx is multi-reader, so separate `ScanAll`
calls (separate read-only txns) never contend with each other, and
different tables' storage/index fields are always disjoint, so there's no
shared mutable state between them either.

## `TableModel.PrimaryKeyAccessor` / `TableModel.Accessor`

The generated primary-key read method's name (default "Get") and the
generated property exposing this table's Ops on the database's Transaction
(default "{RowTypeName}s") - see `PrimaryKeyAttribute.Accessor` /
`TableAttribute.Accessor`.

## `TableModel.ChunkSize`

The generated `DenseArray<TRow>` storage field's chunk size - see
`TableAttribute.ChunkSize` (default 4096).

## `TableModel.Evictable`

Persistent-kind only - see `TableAttribute.Evictable`. Always false for
Instant-kind (RHINO008 rejects setting it true there).

## `IndexModel`

`AccessorName` is the generated method/field name for this index - the
shared Accessor value for a composite index, or a lone field's own name
(see `IndexAttribute.Accessor`). `Fields` is 1 entry for a plain index, 2-3
for a composite one, already sorted by Order/declaration position.

## Local mirror enums `IndexKind`/`Uniqueness`

Mirrors `RhinoDB.Core.Tables.IndexKind`/`Uniqueness` - kept in sync by hand
(this project doesn't reference `RhinoDB.Core`, it only reads attribute
metadata by name).
