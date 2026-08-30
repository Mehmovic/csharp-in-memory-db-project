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
        return Result.Error(new ArgumentException("Duplicate key"));
    }

    public Result<TRow> Get(TKey key) {
        return hashMap.TryGetValue(key, out var index)
            ? storage.Get(index)
            : Result.Error(new KeyNotFoundException($"Key {key} does not exist"));
    }

    public Result Delete(TKey key) {
        if (!hashMap.Remove(key, out var index))
            return Result.Error(new KeyNotFoundException($"Key {key} does not exist"));
        
        if (storage.Delete(index) != DeleteType.DeletedWithSwap)
            return Result.Ok();
        
        TRow swapped = storage.Get(index);
        hashMap[keySelector(swapped)] = index;
        return Result.Ok();
    }
}
