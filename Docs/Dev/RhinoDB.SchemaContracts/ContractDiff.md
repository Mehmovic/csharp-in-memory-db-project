# `src/RhinoDB.SchemaContracts/ContractDiff.cs` — dev notes

## File header

Classifies a schema change between two versions of the SAME table (already matched by identity -
Accessor/TableIdHash - by whatever calls this; an Accessor rename deliberately makes tableId AND the mdbx
sub-db name both change, so it looks exactly like "new empty table + orphan" to any identity-based
matching - per `Docs/06-schema-migration.md` §6, that requires an explicit rename step the caller supplies,
not something `Diff` can infer from two descriptors that no longer share an identity to correlate by).

## `Diff`

`oldTable` null (`newTable` not null) - a brand-new table; nothing existed before it to be incompatible
with, so `Unchanged` (no migration needed), not one of the other three classifications. `oldTable` not
null, `newTable` null - `Removed` (point 6's orphan-table case). Indexes are never inspected here - derived
data (§6's rule: "rebuilt, never migrated"), so an index-only change between two otherwise-identical tables
is always `Unchanged`.

## `DiffFields`

Every old field must still be present, unchanged, at the SAME position - covers add-in-the-middle, remove,
reorder and retype all in one pass: any of those makes some index `i` disagree between old and new before
the tail even matters.

## `ValidateRevisions`

Descriptor consistency check (review point 4's "duplicate/skipped generation number" concern, reframed
against what's actually representable in this data model): `DatabaseGenerationState` only ever records the
CURRENT generation, not a history list, so "duplicate" isn't directly checkable there - the concrete,
checkable symptom the SAME underlying bad-merge scenario would actually produce is a table pinned to a
revision NEWER than its own row type's latest known revision, which can only happen if two independent
branches both bumped the same type's revision number and a merge silently picked the wrong pairing. A
revision LOWER than the type's latest is always fine (that table just hasn't been migrated that far yet -
the normal, expected steady state for most tables most of the time).
