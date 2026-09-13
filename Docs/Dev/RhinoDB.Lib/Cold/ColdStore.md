# `src/RhinoDB.Lib/Cold/ColdStore.cs` — dev notes

Excerpts of the trailing comments trimmed from this file during the
2026-09-13 comment cleanup.

## `SafeNoSync`/`CreateDbi`/`ReadOnlyTxn`/`DefaultUnixMode` constants

- `SafeNoSync = 0x10000` — `MDBX_SAFE_NOSYNC`
- `CreateDbi = 0x40000` — `MDBX_CREATE`
- `ReadOnlyTxn = 0x20000` — `MDBX_RDONLY` / `MDBX_TXN_RDONLY`
- `DefaultUnixMode = 0b110_100_100` — `0644`: `rw-r--r--`

## `Open` — `env.SetGeometry(-1, -1, sizeUpperBytes, -1, -1, -1)`

`-1` everywhere = "keep current or use default" (per `mdbx.h`). `sizeUpperBytes`
(new 2026-09-13, default `-1` preserving the prior always-default behavior for
every existing caller) is the one geometry knob a caller can override — the
max-map-size ceiling. Needed for `RhinoDB.Run.Server.Benchmark`'s
billion-record persistent-table tier: libmdbx's default geometry doesn't
reserve nearly enough address space for that scale, and `sizeUpper` is a
virtual-address-space reservation (mmap-based, pages fault in lazily), not an
upfront disk allocation — safe to set generously large without pre-consuming
that much real disk space.
