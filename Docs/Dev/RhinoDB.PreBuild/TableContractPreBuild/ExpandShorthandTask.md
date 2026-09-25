# `src/RhinoDB.PreBuild/TableContractPreBuild/ExpandShorthandTask.cs` — dev notes

## `ComputeOutputPath`

Mirrors a shorthand file's subdirectory structure under `sourceDir` into `outputDir` -
`RhinoContracts/Tables/Matches/PvPTable.cs` -> `Rhino/Tables/Matches/PvPTable.g.cs`, not flattened.
