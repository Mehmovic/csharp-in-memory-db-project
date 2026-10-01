# `src/RhinoDB.Tools.Migration/ProjectPathArg.cs` — dev notes

## `Resolve`

`--project <path>` if given; otherwise the single `.csproj` in the current directory. Ambiguous (0 or 2+
candidates) with no explicit `--project` is a clear error, not a guess.
