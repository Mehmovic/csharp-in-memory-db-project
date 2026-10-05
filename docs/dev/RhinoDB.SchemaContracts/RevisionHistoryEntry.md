# `src/RhinoDB.SchemaContracts/RevisionHistoryEntry.cs` — dev notes

## File header

One hop in a table's cumulative revision history - "as of this database's generation, this table's row
type was pinned to this revision." Appended (never overwritten) by `migration create` every time a breaking
change bumps this table, so the runtime migration engine can answer "what revision is this table's ON-DISK
data actually in?" from nothing but G_db - the missing piece point D's original design assumed but was
never actually built (confirmed absent in Phase 2.14, then again blocking Phase 4 step 20 until this was
added, 2026-09-25).
