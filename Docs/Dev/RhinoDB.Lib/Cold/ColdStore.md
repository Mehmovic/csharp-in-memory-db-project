# `src/RhinoDB.Lib/Cold/ColdStore.cs` — dev notes

Excerpts of the trailing comments trimmed from this file during the
2026-09-13 comment cleanup.

## `SafeNoSync`/`CreateDbi`/`ReadOnlyTxn`/`DefaultUnixMode` constants

- `SafeNoSync = 0x10000` — `MDBX_SAFE_NOSYNC`
- `CreateDbi = 0x40000` — `MDBX_CREATE`
- `ReadOnlyTxn = 0x20000` — `MDBX_RDONLY` / `MDBX_TXN_RDONLY`
- `DefaultUnixMode = 0b110_100_100` — `0644`: `rw-r--r--`

## `Open` — `env.SetGeometry(-1, -1, -1, -1, -1, -1)`

`-1` everywhere = "keep current or use default" (per `mdbx.h`).
