using RhinoDB.Lib.Indexing;
using RhinoDB.Lib.Storage;

namespace RhinoDB.Lib.Cold;

public class PersistentTable<TKey, TRow>(ColdStore cold, string subDbName, int chunkSize, IUniqueIndex<TKey> primary, Func<TRow, TKey> selector)
    where TKey : IEquatable<TKey>, IComparable<TKey>
    where TRow : struct {
    private readonly DenseArray<TRow> storage = new DenseArray<TRow>(chunkSize: chunkSize);
    private readonly ColdTable<TKey, TRow> coldTable = cold.OpenTable<TKey, TRow>(subDbName);

    public int Count => storage.Count;

    public TRow GetByOffset(int offset) => storage.Get(offset);

    public Result<TRow> Get(TKey id) {
        var located = Locate(id);
        return located.IsError() ? located.Void() : located.Unwrap().Row;
    }

    public Result Insert(TRow record) {
        if (!cold.IsScopeActive) return Result.Error(DbError.NoActiveTransaction());

        Result memResult = InsertIntoStorage(record);
        return memResult.IsError() ? memResult : coldTable.Put(cold.EnsureWriteTxn(), selector(record), record);
    }

    public Result Delete(TKey id) {
        if (!cold.IsScopeActive) return Result.Error(DbError.NoActiveTransaction());

        var located = Locate(id);
        if (located.IsError()) {
            if (located.GetError().Kind != ErrorKind.IndexKeyNotFound) return located.Void();
            Result coldDelete = coldTable.Delete(cold.EnsureWriteTxn(), id);
            return coldDelete.IsOk() ? Result.Ok() : located.Void();
        }

        (TRow record, var offset) = located.Unwrap();
        RemoveFromStorage(record, offset);
        return coldTable.Delete(cold.EnsureWriteTxn(), id);
    }

    public Result Update(TKey id, TRow newRecord) {
        if (!cold.IsScopeActive) return Result.Error(DbError.NoActiveTransaction());

        TKey newId = selector(newRecord);
        if (!id.Equals(newId)) return Result.Error(DbError.PrimaryKeyImmutable());

        var located = Locate(id);
        if (located.IsError()) return located.Void();

        storage.Set(located.Unwrap().Offset, newRecord);
        return coldTable.Put(cold.EnsureWriteTxn(), id, newRecord);
    }

    public Result Load(TKey id) {
        if (Get(id).IsOk()) return Result.Ok();
        if (!cold.IsScopeActive) return Result.Error(DbError.NoActiveTransaction());

        var coldResult = coldTable.Get(cold.EnsureWriteTxn(), id);
        return coldResult.IsError() ? coldResult.Void() : InsertIntoStorage(coldResult.Unwrap());
    }

    public Result Evict(TKey id) {
        var located = Locate(id);
        if (located.IsError()) {
            return located.GetError().Kind == ErrorKind.IndexKeyNotFound ? Result.Ok() : located.Void();
        }

        (TRow record, var offset) = located.Unwrap();
        RemoveFromStorage(record, offset);
        return Result.Ok();
    }

    public Result<TRow> Peek(TKey id) => cold.Peek(coldTable, id);

    private Result InsertIntoStorage(TRow record) {
        TKey pk = selector(record);
        if (primary.GetOffset(pk).IsOk()) return Result.Error(DbError.DuplicateKey());

        var offset = storage.Insert(record);
        primary.Insert(pk, offset);
        return Result.Ok();
    }

    private void RemoveFromStorage(TRow record, int offset) {
        var lastOffset = storage.LastOffset;
        var swapped = storage.Delete(offset);
        primary.Delete(selector(record));

        if (swapped is not { } swappedRow) return;

        TKey swappedPk = selector(swappedRow);
        primary.Delete(swappedPk);
        primary.Insert(swappedPk, offset);
    }

    private Result<(TRow Row, int Offset)> Locate(TKey id) {
        var offsetResult = primary.GetOffset(id);
        if (offsetResult.IsError()) return offsetResult.Void();

        var offset = offsetResult.Unwrap();
        return (storage.Get(offset), offset);
    }
}
