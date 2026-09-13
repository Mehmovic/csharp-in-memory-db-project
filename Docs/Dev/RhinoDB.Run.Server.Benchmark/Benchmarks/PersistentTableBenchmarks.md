# `src/RhinoDB.Run.Server.Benchmark/Benchmarks/PersistentTableBenchmarks.cs` — dev notes

## `MapSizeUpperBytes`

128 GB reservation, not upfront disk usage — `sizeUpper` is a virtual-address-
space reservation (mmap-based, pages fault in lazily), not a real disk
allocation, so it's safe to size generously for the billion-record tier
without pre-consuming that much real disk space. See
`Docs/Dev/RhinoDB.Lib/Cold/ColdStore.md` for the `ColdStore.Open` side of this.

## `Setup` — `estimatedBytes`/`sizeNowBytes`

Pre-sizes `sizeNow` past the seeded data, found necessary by a real
measurement artifact: with `sizeNow` left at its default, the seeded database
sits right at wherever incremental growth last left it, so the *next* write -
the one a `[Benchmark]` method is timing - could itself trigger a growth/remap
event, a real cost that isn't the cost being measured. This is why the
small-tier `Insert`/`Update` numbers originally came back *slower* than the
large-tier ones (backwards from what flat, size-independent operations should
show) - the small tiers' files were still near a growth boundary when the
timed write ran.

64 bytes/record is a deliberately generous per-record overhead estimate for
`PersistentWidget`'s 12-byte payload (libmdbx B+-tree page/slot overhead
dominates over the payload itself for a record this small) - the exact
number matters less than staying comfortably above actual usage, since this
is a benchmark setup hint, not a correctness-critical value. The flat +64MB
on top only needs to cover the benchmark's own incidental extra inserts
during measurement (at most a few thousand across all iterations), not
another `RecordCount`'s worth - it doesn't need to scale with `RecordCount`
at all. Capped at `MapSizeUpperBytes` via `Math.Min` since `sizeNow` can
never exceed `sizeUpper`; only matters at the largest opt-in tiers where the
generous per-record estimate would otherwise overshoot the 128 GB ceiling.
