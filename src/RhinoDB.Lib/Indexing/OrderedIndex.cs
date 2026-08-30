using RhinoDB.Lib.Storage;

namespace RhinoDB.Lib.Indexing;

public class OrderedIndex<TKey, TRow>(DenseArray<TRow> storage, Func<TRow, TKey> keySelector)
    where TKey : notnull
    where TRow : struct {
    private readonly SortedList<TKey, int> sortedList = new SortedList<TKey, int>();
    
    public int Count => storage.Count;

    public Result Insert(TRow row) {
        TKey key = keySelector(row);

        var index = storage.Insert(row);
        if (sortedList.TryAdd(key, index)) return Result.Ok();

        storage.Delete(index);
        return Result.Error(new ArgumentException("Duplicate key"));
    }

    public Result<TRow> Get(TKey key) {
        return sortedList.TryGetValue(key, out var index)
            ? storage.Get(index)
            : Result.Error(new KeyNotFoundException($"Key {key} does not exist"));
    }

    public Result Delete(TKey key) {
        if (!sortedList.Remove(key, out var index))
            return Result.Error(new KeyNotFoundException($"Key {key} does not exist"));
        
        if (storage.Delete(index) != DeleteType.DeletedWithSwap)
            return Result.Ok();
        
        TRow swapped = storage.Get(index);
        sortedList[keySelector(swapped)] = index;
        return Result.Ok();
    }
    
    public IEnumerable<TRow> Range(TKey from , TKey to) {
        var lo = LowerBound(sortedList.Keys, from); // first index with key >= from
        var hi = UpperBound(sortedList.Keys, to);   // first index with key >  to

        for (var i = lo; i < hi; i++)
            yield return storage.Get(sortedList.Values[i]);
    }
    
    static private int LowerBound(IList<TKey> keys, TKey key) {
        int low = 0, keyCount = keys.Count;
        var cmp = Comparer<TKey>.Default;
        while (low < keyCount) {
            var m = low + ((keyCount - low) >> 1);
            if (cmp.Compare(keys[m], key) < 0) low = m + 1;
            else keyCount = m;
        }
        return low;
    }

    static private int UpperBound(IList<TKey> keys, TKey key) {
        int low = 0, keyCount = keys.Count;
        var cmp = Comparer<TKey>.Default;
        while (low < keyCount) {
            var m = low + ((keyCount - low) >> 1);
            if (cmp.Compare(keys[m], key) <= 0) low = m + 1;
            else keyCount = m;
        }
        return low;
    }
}
