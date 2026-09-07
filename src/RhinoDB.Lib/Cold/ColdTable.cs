using MemoryPack;
using RhinoDB.Native;

namespace RhinoDB.Lib.Cold;

internal sealed class ColdTable<TKey, TRow>(uint dbi)
    where TKey : IEquatable<TKey>, IComparable<TKey>
    where TRow : struct {
    internal uint Dbi { get; } = dbi;

    internal Result Put(Transaction txn, TKey key, TRow row) {
        var keyBytes = MemoryPackSerializer.Serialize(key);
        var rowBytes = MemoryPackSerializer.Serialize(row);
        var rc = txn.Put(Dbi, keyBytes, rowBytes, flags: 0);
        return rc == 0 ? Result.Ok() : Result.Error(MdbxErrorMapper.Map(rc));
    }

    internal Result<TRow> Get(Transaction txn, TKey key) {
        var keyBytes = MemoryPackSerializer.Serialize(key);
        var rc = txn.Get(Dbi, keyBytes, out var rowBytes);
        if (rc != 0) return Result<TRow>.Error(MdbxErrorMapper.Map(rc));
        return MemoryPackSerializer.Deserialize<TRow>(rowBytes)!;
    }

    internal Result Delete(Transaction txn, TKey key) {
        var keyBytes = MemoryPackSerializer.Serialize(key);
        var rc = txn.Delete(Dbi, keyBytes);
        return rc == 0 ? Result.Ok() : Result.Error(MdbxErrorMapper.Map(rc));
    }
}
