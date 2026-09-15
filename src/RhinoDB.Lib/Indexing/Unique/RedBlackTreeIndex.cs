namespace RhinoDB.Lib.Indexing;

public class RedBlackTreeIndex<TKey> : IUniqueIndex<TKey>
    where TKey : notnull {
    private readonly SortedSet<(TKey Key, int Offset)> sortedSet =
        new SortedSet<(TKey Key, int Offset)>(
            Comparer<(TKey Key, int _)>.Create((a, b) => Comparer<TKey>.Default.Compare(a.Key, b.Key))
        );

    public int Count => sortedSet.Count;

    public Result<int> GetOffset(TKey key) {
        return sortedSet.TryGetValue((key, 0), out (TKey Key, int Offset) entry)
            ? entry.Offset
            : Result.Error(DbError.IndexKeyNotFound());
    }

    public void Insert(TKey key, int offset) {
        sortedSet.Add((key, offset));
    }

    public void Delete(TKey key) {
        sortedSet.Remove((key, 0));
    }

    public void Range(TKey from, TKey to, ICollection<int> into) {
        if (Comparer<TKey>.Default.Compare(from, to) > 0) return;

        foreach ((TKey Key, int Offset) entry in sortedSet.GetViewBetween((from, 0), (to, 0)))
            into.Add(entry.Offset);
    }
}
