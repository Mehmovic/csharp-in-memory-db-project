# `src/RhinoDB.Run.Server.Benchmark/Benchmarks/PersistentTableBenchmarks.cs` — dev notes

## `MapSizeUpperBytes`

128 GB reservation, not upfront disk usage — `sizeUpper` is a virtual-address-
space reservation (mmap-based, pages fault in lazily), not a real disk
allocation, so it's safe to size generously for the billion-record tier
without pre-consuming that much real disk space. See
`Docs/Dev/RhinoDB.Lib/Cold/ColdStore.md` for the `ColdStore.Open` side of this.
