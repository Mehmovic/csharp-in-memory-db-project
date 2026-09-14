# `src/RhinoDB.Lib/Durability/WalRecordCodec.cs` — dev notes

## Entry layout

`Docs/05-wal-design.md` Phase 1, amended for the operation-level LSN resolution (one
entry per operation, not per change):

```
[u32 length][u32 checksum][u64 lsn][byte kind][payload]
```

The checksum covers everything from `lsn` through the end of `payload` — never the
`length` or `checksum` fields themselves. `payload` is MemoryPack-encoded `WalChange[]`
for `Operation` entries, empty for `CheckpointMarker` entries (the `lsn` alone is the
watermark).

## `TryDecode` — torn-tail vs. mid-file corruption

A checksum mismatch is only ever reported as `TornTail` when this record sits exactly
at the end of the supplied buffer (nothing follows it) — a genuine crash-mid-append
leaves a trailing partial/garbled frame with nothing after it. A checksum mismatch with
further bytes trailing it means real corruption struck an already-fsync'd record, which
should never happen and is treated as fatal.
