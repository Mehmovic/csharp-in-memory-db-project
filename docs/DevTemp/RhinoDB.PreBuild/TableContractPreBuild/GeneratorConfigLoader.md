# `src/RhinoDB.PreBuild/TableContractPreBuild/GeneratorConfigLoader.cs` — dev notes

## `TryWriteDefaultFile`

Best-effort - a developer editing this file later is the point (real, visible, editable settings instead
of an invisible in-memory fallback), but failing to scaffold it must never fail the build, since the tool
works fine without it either way.
