using RhinoDB.Lib.Storage;

namespace RhinoDB.Lib.Indexing;

public class OrderedIndex<TKey, TRow>(DenseArray<TRow> storage, Func<TRow, TKey> keySelector)
    where TKey : notnull
    where TRow : struct {
    private readonly SortedSet<(TKey Key, int Offset)> sortedSet =
        new SortedSet<(TKey Key, int Offset)>(
            Comparer<(TKey Key, int _)>.Create((a, b) => Comparer<TKey>.Default.Compare(a.Key, b.Key))
        );

    public int Count => storage.Count;

    public Result Insert(TRow row) {
        TKey key = keySelector(row);

        var index = storage.Insert(row);
        if (sortedSet.Add((key, index))) return Result.Ok();

        storage.Delete(index);
        return Result.Error(new DuplicateKeyException(key));
    }

    public Result<TRow> Get(TKey key) {
        return sortedSet.TryGetValue((key, 0), out (TKey Key, int Offset) entry)
            ? storage.Get(entry.Offset)
            : Result.Error(new IndexKeyNotFoundException(key));
    }

    public Result<int> GetOffset(TKey key) {
        return sortedSet.TryGetValue((key, 0), out (TKey Key, int Offset) entry)
            ? entry.Offset
            : Result.Error(new IndexKeyNotFoundException(key));
    }

    public Result<(TRow, int)> Fetch(TKey key) {
        return sortedSet.TryGetValue((key, 0), out (TKey Key, int Offset) entry)
            ? (storage.Get(entry.Offset), entry.Offset)
            : Result.Error(new IndexKeyNotFoundException(key));
    }

    public Result Delete(TKey key) {
        if (!sortedSet.TryGetValue((key, 0), out (TKey Key, int Offset) entry))
            return Result.Error(new IndexKeyNotFoundException(key));

        sortedSet.Remove(entry);

        if (storage.Delete(entry.Offset) != DeleteType.DeletedWithSwap)
            return Result.Ok();

        TRow swapped = storage.Get(entry.Offset);
        TKey swappedKey = keySelector(swapped);
        sortedSet.Remove((swappedKey, 0));
        sortedSet.Add((swappedKey, entry.Offset));
        return Result.Ok();
    }

    public Result Register(TKey key, int offset) {
        return sortedSet.Add((key, offset))
            ? Result.Ok()
            : Result.Error(new DuplicateKeyException(key));
    }

    public Result Deregister(TKey key, int offset) {
        if (!sortedSet.TryGetValue((key, 0), out (TKey Key, int Offset) entry))
            return Result.Error(new IndexKeyNotFoundException(key));

        if (entry.Offset != offset)
            return Result.Error(new OffsetNotRegisteredException(key, offset));

        sortedSet.Remove(entry);
        return Result.Ok();
    }

    public List<TRow> Range(TKey from, TKey to) {
        if (Comparer<TKey>.Default.Compare(from, to) > 0) return [];

        var rows = new List<TRow>();
        foreach ((TKey Key, int Offset) entry in sortedSet.GetViewBetween((from, 0), (to, 0)))
            rows.Add(storage.Get(entry.Offset));

        return rows;
    }
}
