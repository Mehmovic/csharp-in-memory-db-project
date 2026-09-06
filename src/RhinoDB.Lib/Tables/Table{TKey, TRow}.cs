using RhinoDB.Lib.Indexing;
using RhinoDB.Lib.Storage;

namespace RhinoDB.Lib.Tables;

public class Table<TKey, TRow>(int chunkSize, IUniqueIndex<TKey> primary, Func<TRow, TKey> selector)
    where TKey : IEquatable<TKey>, IComparable<TKey>
    where TRow : struct {
    private readonly DenseArray<TRow> storage = new DenseArray<TRow>(chunkSize: chunkSize);
    private readonly List<ISecondaryIndex<TRow>> secondaryIndexes = [];

    public int Count => storage.Count;

    public void Register(ISecondaryIndex<TRow> secondaryIndex) {
        secondaryIndexes.Add(secondaryIndex);
    }

    public TRow GetByOffset(int offset) {
        return storage.Get(offset);
    }

    public Result<TRow> Get(TKey id) {
        var located = Locate(id);
        if (located.IsError()) return located.Void();
        return located.Unwrap().Row;
    }

    public Result Insert(TRow record) {
        TKey pk = selector(record);
        var duplicateIndexResult = primary.GetOffset(pk);
        if (duplicateIndexResult.IsOk()) { return Result.Error(DbError.DuplicateKey()); }

        foreach (var idx in secondaryIndexes) {
            Result insertResult = idx.CheckInsert(record, -1);
            if (insertResult.IsError()) { return insertResult; }
        }

        var offset = storage.Insert(record);

        primary.Insert(pk, offset);

        foreach (var idx in secondaryIndexes) {
            idx.Insert(record, offset);
        }
        return Result.Ok();
    }

    public Result Delete(TKey id) {
        var located = Locate(id);
        if (located.IsError()) return located;

        (TRow record, var offset) = located.Unwrap();

        var lastOffset = storage.LastOffset;
        var swapped = storage.Delete(offset);

        primary.Delete(selector(record));
        foreach (var idx in secondaryIndexes) {
            idx.Delete(record, offset);
        }

        if (swapped is not { } swappedRow) return Result.Ok();

        
        TKey swappedPk = selector(swappedRow);

        primary.Delete(swappedPk);
        foreach (var idx in secondaryIndexes) {
            idx.Delete(swappedRow, lastOffset);
        }

        primary.Insert(swappedPk, offset);
        foreach (var idx in secondaryIndexes) {
            idx.Insert(swappedRow, offset);
        }

        return Result.Ok();
    }

    public Result Update(TKey id, TRow newRecord) {
        TKey newId = selector(newRecord);
        if (!id.Equals(newId)) return Result.Error(DbError.PrimaryKeyImmutable());

        var located = Locate(id);
        if (located.IsError()) return located;

        (TRow oldRecord, var offset) = located.Unwrap();

        foreach (var idx in secondaryIndexes) {
            Result result = idx.CheckInsert(newRecord, offset);
            if (result.IsError()) { return result; }
        }

        foreach (var idx in secondaryIndexes) {
            idx.Delete(oldRecord, offset);
            idx.Insert(newRecord, offset);
        }

        storage.Set(offset, newRecord);
        return Result.Ok();
    }

    private Result<(TRow Row, int Offset)> Locate(TKey id) {
        var offsetResult = primary.GetOffset(id);
        if (offsetResult.IsError()) return offsetResult.Void();

        var offset = offsetResult.Unwrap();
        return (storage.Get(offset), offset);
    }
}
