namespace RhinoDB.Lib.Cold;

// The two generated-code callbacks ColdStore needs to apply an eviction batch for one
// table (Docs/05-wal-design.md Phase 3 - the eviction write-through):
//   TryGetCurrentRow: the row's CURRENT serialized value, or null if the row is no longer
//     resident (deleted or already evicted since staging) - re-read at batch-apply time so
//     an update between staging and apply writes the current value, never a stale one.
//   Drop: remove the row from memory (swap-remove + index maintenance) - executed only
//     after the batch's mdbx commit succeeded, so memory is never ahead of the durable copy.
public readonly record struct EvictionDropRegistration(Func<byte[], byte[]?> TryGetCurrentRow, Action<byte[]> Drop);
