# `src/RhinoDB.Lib/Durability/CheckpointEngine.cs` — dev notes

## `ReadGeneration`

G_db - defaults to 0 for a database that has never recorded a generation (fresh database, or one that
predates this field existing), matching G_binary's own default-0 behavior for a schema that has never had
a breaking change.

## `WriteGeneration`

Writes into an ALREADY-OPEN, caller-owned transaction rather than opening its own - per `Docs/06-schema-
migration.md` §3, the generation must be written in the SAME transaction as the row rewrites it accompanies
(the migration transaction, Phase 4 step 20), so "migrated" and "generation bumped" can never disagree. The
caller commits.
