using RhinoDB.Lib.Storage;

namespace RhinoDB.Lib.Indexing;

public class NonUniqueOrderedIndex<TKey, TRow>(DenseArray<TRow> storage)
    where TKey : notnull
    where TRow : struct {
    private readonly SortedSet<(TKey Key, int Offset)> sortedSet = new SortedSet<(TKey Key, int Offset)>(
        Comparer<(TKey Key, int Offset)>.Create((a, b) => {
                var cmp = Comparer<TKey>.Default.Compare(a.Key, b.Key);
                return cmp != 0 ? cmp : a.Offset.CompareTo(b.Offset);
            }
        )
    );

    public Result Register(TKey key, int offset) {
        sortedSet.Add((key, offset));
        return Result.Ok();
    }

    public Result Deregister(TKey key, int offset) {
        return sortedSet.Remove((key, offset))
            ? Result.Ok()
            : Result.Error(new OffsetNotRegisteredException(key, offset));
    }

    public List<TRow> Get(TKey key) => Range(key, key);

    public List<TRow> Range(TKey from, TKey to) {
        if (Comparer<TKey>.Default.Compare(from, to) > 0) return [];

        var rows = new List<TRow>();
        foreach ((TKey Key, int Offset) entry in sortedSet.GetViewBetween((from, int.MinValue), (to, int.MaxValue)))
            rows.Add(storage.Get(entry.Offset));

        return rows;
    }
}
