# `src/RhinoDB.Tools.Migration/MigrationCreateCommand.cs` — dev notes

## File header

`rhinodb migration create` - for every table whose Breaking change has no counterpart yet in the committed
Descriptor.json: freezes the OLD shape as a real, compilable `[FrozenSchema(Revision=N)]` type, scaffolds an
empty `[Migration(FromRevision=N)]` stub for the developer to fill in, and bumps Descriptor.json (the row
type's own revision once, every owning database's generation once - grouped by row type, not by table, so a
type shared across several tables/databases never gets double-bumped).

**v1 scope, deliberately not the full design**: only Breaking changes are processed here. AdditiveOnly
changes are NOT bumped by this command - the generator auto-synthesizes those at build time with no
`[Migration]` required, and this command intentionally leaves their Descriptor.json revision/generation
untouched rather than guessing whether "changed" should include them. A future pass can extend this once a
real need for AdditiveOnly's own generation bump surfaces (point B's network-compatibility gate is the
eventual consumer, not anything built yet).

## `Run` — generation carry-forward

`CompilationWalker.BuildDescriptor` always builds a FRESH `DatabaseGenerationState` per database
(`Generation` defaults to 0, since it has no access to the committed descriptor) - carry the real,
previously-persisted values forward before anything below increments them, or a second `migration create`
run against an already-migrated database would silently reset its generation to 0 and increment from there
instead of from its real value.

## `Run` — database generation bump ordering

Bump every distinct affected database's generation FIRST, so each table below can record its OWN
database's post-increment value alongside the new revision - the runtime migration engine's only way to
answer "what revision is this table's on-disk data actually in?" given nothing but `G_db`.

## `Run` — `RevisionHistory` carry-forward

Carry the OLD table's history forward (it doesn't exist on the freshly-built `newTable` at all) before
appending this run's own hop.
