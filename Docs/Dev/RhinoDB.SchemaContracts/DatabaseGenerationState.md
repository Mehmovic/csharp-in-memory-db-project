# `src/RhinoDB.SchemaContracts/DatabaseGenerationState.cs` — dev notes

## `RetainedFromGeneration`

The floor this database's archive/migration-chain retention is guaranteed back to - moved forward only by
an explicit prune operation (`RhinoRunMode.Prune`), never silently. RHINO020 checks the developer's
`[Migration(FromRevision=N)]` chain is hole-free from THIS value to the tip, not from "however far back
archives happen to go" (which the generator has no way to know at compile time). Defaults to 0 -
"everything since the beginning is still guaranteed retained."
