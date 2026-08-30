using RhinoDB.Core.Storage;
using RhinoDB.Lib.Storage;

namespace RhinoDB.Lib.Indexing;

public class HashIndex<TKey, TRow>(DenseArray<TRow> storage, Func<TRow, TKey> keySelector)
    where TKey : notnull
    where TRow : struct {
    private readonly Dictionary<TKey, int> hashMap = new Dictionary<TKey, int>(storage.Capacity);

    public int Count => storage.Count;

    public void Insert(TRow row) {
        TKey key = keySelector(row);

        var index = storage.Insert(row);
        if (hashMap.TryAdd(key, index)) return;
        
        storage.Delete(index);
        throw new ArgumentException("Duplicate key");
    }

    public TRow Get(TKey key) {
        return hashMap.TryGetValue(key, out var index)
            ? storage.Get(index)
            : throw new KeyNotFoundException($"Key {key} does not exist");
    }

    public void Delete(TKey key) {
        if (hashMap.Remove(key, out var index)) {
            if (storage.Delete(index) != DeleteType.DeletedWithSwap) return;
            TRow swapped = storage.Get(index);
            hashMap[keySelector(swapped)] = index;
            return;
        }
        throw new KeyNotFoundException($"Key {key} does not exist");
    }
}
