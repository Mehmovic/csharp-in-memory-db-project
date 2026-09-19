using RhinoDB.Native;

namespace RhinoDB.Lib.Cold;

public sealed class ColdTable<TKey, TRow>(
    uint dbi,
    Func<TKey, byte[]> serializeKey,
    Func<byte[], TKey> deserializeKey,
    Func<byte[], TRow> deserializeRow
)
    where TKey : IEquatable<TKey>, IComparable<TKey>
    where TRow : struct {
    internal uint Dbi { get; } = dbi;

    internal Result<TRow> Get(Transaction txn, TKey key) {
        var keyBytes = serializeKey(key);
        var rc = txn.Get(Dbi, keyBytes, out var rowBytes);
        if (rc != 0) return Result<TRow>.Error(MdbxErrorMapper.Map(rc));
        return deserializeRow(rowBytes);
    }

    internal IEnumerable<(TKey Key, TRow Row)> ScanAll(Transaction txn) {
        var rc = txn.OpenCursor(Dbi, out var cursor);
        if (rc != 0) throw MdbxErrorMapper.Map(rc).ToException();

        using (cursor) {
            var getRc = cursor!.GetFirst(out var keyBytes, out var valueBytes);
            while (getRc == 0) {
                yield return (deserializeKey(keyBytes), deserializeRow(valueBytes));
                getRc = cursor.GetNext(out keyBytes, out valueBytes);
            }
        }
    }
}
