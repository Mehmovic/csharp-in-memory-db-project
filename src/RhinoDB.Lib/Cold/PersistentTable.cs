using RhinoDB.Lib.Indexing;
using RhinoDB.Lib.Tables;

namespace RhinoDB.Lib.Cold;

public class PersistentTable<TKey, TRow>(ColdStore cold, string subDbName, int chunkSize, IUniqueIndex<TKey> primary, Func<TRow, TKey> selector)
    where TKey : IEquatable<TKey>, IComparable<TKey>
    where TRow : struct {
    private readonly Table<TKey, TRow> memory = new Table<TKey, TRow>(chunkSize, primary, selector);
    private readonly ColdTable<TKey, TRow> coldTable = cold.OpenTable<TKey, TRow>(subDbName);

    public int Count => memory.Count;

    public void Register(ISecondaryIndex<TRow> secondaryIndex) => memory.Register(secondaryIndex);

    public TRow GetByOffset(int offset) => memory.GetByOffset(offset);

    public Result<TRow> Get(TKey id) => memory.Get(id);

    public Result Insert(TRow record) {
        if (!cold.IsScopeActive) return Result.Error(DbError.NoActiveTransaction());

        Result memResult = memory.Insert(record);
        return memResult.IsError()
            ? memResult
            : coldTable.Put(cold.EnsureWriteTxn(), selector(record), record);
    }

    public Result Delete(TKey id) {
        if (!cold.IsScopeActive) return Result.Error(DbError.NoActiveTransaction());

        Result memResult = memory.Delete(id);

        if (memResult.IsOk()) {
            return coldTable.Delete(cold.EnsureWriteTxn(), id);
        }

        if (memResult.GetError().Kind != ErrorKind.IndexKeyNotFound) return memResult;
        
        Result coldDelete = coldTable.Delete(cold.EnsureWriteTxn(), id);
        return coldDelete.IsOk() ? Result.Ok() : memResult;
    }

    public Result Update(TKey id, TRow newRecord) {
        if (!cold.IsScopeActive) return Result.Error(DbError.NoActiveTransaction());

        Result memResult = memory.Update(id, newRecord);
        return memResult.IsError()
            ? memResult
            : coldTable.Put(cold.EnsureWriteTxn(), id, newRecord);
    }

    public Result Load(TKey id) {
        if (memory.Get(id).IsOk()) return Result.Ok();
        if (!cold.IsScopeActive) return Result.Error(DbError.NoActiveTransaction());

        var coldResult = coldTable.Get(cold.EnsureWriteTxn(), id);
        return coldResult.IsError()
            ? coldResult.Void()
            : memory.Insert(coldResult.Unwrap());
    }

    public Result Evict(TKey id) {
        Result result = memory.Delete(id);
        if (result.IsOk()) return Result.Ok();
        return result.GetError().Kind == ErrorKind.IndexKeyNotFound ? Result.Ok() : result;
    }

    public Result<TRow> Peek(TKey id) => cold.Peek(coldTable, id);
}
