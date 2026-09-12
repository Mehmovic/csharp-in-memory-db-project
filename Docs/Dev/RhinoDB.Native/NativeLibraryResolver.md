# `src/RhinoDB.Native/NativeLibraryResolver.cs` — dev notes

Excerpt of the class-level comment trimmed from this file during the
2026-09-13 comment cleanup.

## `NativeLibraryResolver`

.NET's default P/Invoke probing only walks into `runtimes/{rid}/native/`
automatically when the consuming app was restored with RID-aware asset
resolution (a real NuGet package reference, or an explicit
`RuntimeIdentifier` at publish time). A plain `ProjectReference` - which is
all RhinoDB.Lib/the test projects use today - copies `mdbx.dll`/`libmdbx.so`
there via this project's `.csproj`, but the loader never looks: this
resolver is what actually points "mdbx" at the right file.
