using RhinoDB.Lib.Storage;

namespace RhinoDB.Lib.Indexing;

public class HashIndex<TKey, TRow>(DenseArray<TRow> storage, Func<TRow, TKey> keySelector)
    where TKey : notnull
    where TRow : struct {
    private readonly Dictionary<TKey, int> hashMap = new Dictionary<TKey, int>(storage.Capacity);

    public int Count => storage.Count;

    public Result Insert(TRow row) {
        TKey key = keySelector(row);

        var index = storage.Insert(row);
        if (hashMap.TryAdd(key, index)) return Result.Ok();

        storage.Delete(index);
        return Result.Error(new DuplicateKeyException(key));
    }

    public Result<TRow> Get(TKey key) {
        return hashMap.TryGetValue(key, out var index)
            ? storage.Get(index)
            : Result.Error(new IndexKeyNotFoundException(key));
    }

    public Result<int> GetOffset(TKey key) {
        return hashMap.TryGetValue(key, out var index)
            ? index
            : Result.Error(new IndexKeyNotFoundException(key));
    }

    public Result Delete(TKey key) {
        if (!hashMap.Remove(key, out var index))
            return Result.Error(new IndexKeyNotFoundException(key));

        if (storage.Delete(index) != DeleteType.DeletedWithSwap)
            return Result.Ok();

        TRow swapped = storage.Get(index);
        hashMap[keySelector(swapped)] = index;
        return Result.Ok();
    }

    public Result Register(TKey key, int offset) {
        return hashMap.TryAdd(key, offset)
            ? Result.Ok()
            : Result.Error(new DuplicateKeyException(key));
    }

    public Result Deregister(TKey key, int offset) {
        if (!hashMap.TryGetValue(key, out var storedOffset))
            return Result.Error(new IndexKeyNotFoundException(key));

        if (storedOffset != offset)
            return Result.Error(new OffsetNotRegisteredException(key, offset));

        hashMap.Remove(key);
        return Result.Ok();
    }
}
