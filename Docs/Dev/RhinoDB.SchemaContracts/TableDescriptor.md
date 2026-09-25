# `src/RhinoDB.SchemaContracts/TableDescriptor.cs` — dev notes

## `DatabaseFullName`

Which `[Database]` this table belongs to - required because one row type can be embedded by `[Table]`
attributes on several different databases at once (multi-database tables), each with its own `Accessor` and
its own independently-scheduled migration/pinned revision (point D).

## `Revision`

The row type's own revision (point D of the schema-migration design: decoupled from any database's
generation) that this table is currently pinned to. Shared verbatim across every database that embeds the
same row type.

## `Fields`

Fully expanded/flattened through every `[CustomType]` boundary, recursively, using dotted paths (e.g.
`"Loadout.WeaponId"`) - see point A. This is what makes N-1-vs-N diffing correct without any special-casing
for a change buried inside a shared CustomType.

## `RevisionHistory`

Cumulative, ascending-by-Generation - never overwritten, only appended to by `migration create`. The
runtime migration engine looks up "the largest entry whose Generation <= G_db" to find which revision this
table's ON-DISK data is actually in; an empty list (or no entry <= G_db) means revision 0 - the row type's
original, never-yet-migrated shape.

## `RemovedAtGeneration`

Set once a table is observed missing from a live compilation (point 6's "Removed" classification) - null
while the table still exists live. Orphan retention (doc §6/§11: kept one generation, then dropped): the
drop condition is `RemovedAtGeneration < database's current generation` (equivalently `<=` the generation
immediately before current) - deliberately NOT "exactly one generation ago," since a database can skip
several generations in one migration run (e.g. offline through 3 breaking changes, migrating 4->7 in a
single pass) and the orphan must still be dropped correctly regardless of how many generations were
actually skipped.
