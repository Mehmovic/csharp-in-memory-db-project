# `src/RhinoDB.Lib/Cold/ColdStore.cs` — dev notes

Excerpts of the trailing comments trimmed from this file during the
2026-09-13 comment cleanup.

## `SafeNoSync`/`CreateDbi`/`ReadOnlyTxn`/`DefaultUnixMode` constants

- `SafeNoSync = 0x10000` — `MDBX_SAFE_NOSYNC`
- `CreateDbi = 0x40000` — `MDBX_CREATE`
- `ReadOnlyTxn = 0x20000` — `MDBX_RDONLY` / `MDBX_TXN_RDONLY`
- `DefaultUnixMode = 0b110_100_100` — `0644`: `rw-r--r--`

## `Open` — `env.SetGeometry(-1, sizeNowBytes, sizeUpperBytes, -1, -1, -1)`

`-1` everywhere = "keep current or use default" (per `mdbx.h`). `sizeUpperBytes`
(2026-09-13, default `-1` preserving the prior always-default behavior for
every existing caller) is the max-map-size ceiling. Needed for
`RhinoDB.Run.Server.Benchmark`'s billion-record persistent-table tier:
libmdbx's default geometry doesn't reserve nearly enough address space for
that scale, and `sizeUpper` is a virtual-address-space reservation (mmap-based,
pages fault in lazily), not an upfront disk allocation — safe to set
generously large without pre-consuming that much real disk space.

`sizeNowBytes` (also 2026-09-13, same default-preserving `-1`) sets the
*current* size instead of leaving it to grow incrementally from libmdbx's
small default. Added after a real benchmark artifact: with `sizeNow` left at
-1, a freshly-seeded database sits right at whatever size incremental growth
last left it at, so the *next* write (the one a benchmark is trying to time)
can itself trigger a growth/remap event - a real cost, but not the cost being
measured, and it explains why `RhinoDB.Run.Server.Benchmark`'s small-tier
Insert/Update numbers were originally *slower* than the large-tier ones
(backwards from what flat, size-independent operations should show). A
caller that knows its expected data size up front (like a benchmark's
`GlobalSetup`) can pre-size `sizeNow` past that, so ordinary measured writes
never pay for growth they didn't ask for.
