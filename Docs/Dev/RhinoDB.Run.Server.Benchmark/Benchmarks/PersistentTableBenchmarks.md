# `src/RhinoDB.Run.Server.Benchmark/Benchmarks/PersistentTableBenchmarks.cs` — dev notes

## `MapSizeUpperBytes`

128 GB reservation, not upfront disk usage — `sizeUpper` is a virtual-address-
space reservation (mmap-based, pages fault in lazily), not a real disk
allocation, so it's safe to size generously for the billion-record tier
without pre-consuming that much real disk space. See
`Docs/Dev/RhinoDB.Lib/Cold/ColdStore.md` for the `ColdStore.Open` side of this.

## `Setup` — `estimatedBytes`/`sizeNowBytes`

Pre-sizes `sizeNow` past the seeded data, on the theory that a database
sitting right at wherever incremental growth last left it could make the
*next* write - the one a `[Benchmark]` method is timing - trigger a
growth/remap event of its own. A real, correct thing to want independent of
anything else, but **this was not what caused the small-tier
`Insert`/`Update` numbers to come back slower than the large-tier ones** -
see the warm-up churn note below for the actual cause, found by re-measuring
after this change and finding the pattern unchanged.

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

## `Setup` — fixed warm-up churn (2026-09-13)

Inserts 20,000 throwaway rows (a key range disjoint from the real seeded
data, e.g. negative IDs) under `Confirmed`, independent of `RecordCount`,
right after seeding. Found necessary by measurement, not assumed: a freshly-
created file's never-before-written regions cost roughly 10x more to write
to on this dev machine than already-settled ones (Windows Defender real-time
scanning is the leading suspect, confirmed active via `Get-MpComputerStatus`
- see `Docs/03-roadmap.md`'s 2026-09-13 entry for the full two-experiment
diagnosis), which is what actually produced the earlier backwards-looking
small-tier-slower-than-large-tier pattern - nothing to do with `RecordCount`
or libmdbx geometry at all. This warm-up fixes `Update` (which always
rewrites the same fixed `lookupKey`, so once warm-up settles that page every
subsequent `Update` is cheap) but **does not** fix `Insert` - `Insert`
always writes a brand-new, monotonically increasing key, so it inherently
touches a never-before-written page on every single call regardless of how
much unrelated warm-up already ran. That's not a benchmark bug to route
around: a real ever-growing, append-only table has the same property, so
`Insert`'s measured cost genuinely reflects sustained-append behavior on a
machine like this one, not an artifact to be warmed away. Kept as legitimate
methodology (a real long-running database's file is never actually cold
either) rather than removed once its actual purpose was understood.

## `Setup` — `.AsTask()` before `.GetAwaiter().GetResult()` (2026-09-14)

Required once `Run` switched to returning `ValueTask<Result>` - `ValueTask<T>`
does not support a blocking synchronous wait the way `Task<T>.GetAwaiter()
.GetResult()` does, and calling it directly right after enqueueing throws
`InvalidOperationException`. See `Docs/Dev/RhinoDB.Lib/Execution/PooledOperation.md`.
