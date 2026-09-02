namespace RhinoDB.Lib.Indexing;

public class OrderedIndex<TKey>
    where TKey : notnull {
    private readonly SortedSet<(TKey Key, int Offset)> sortedSet =
        new SortedSet<(TKey Key, int Offset)>(
            Comparer<(TKey Key, int _)>.Create((a, b) => Comparer<TKey>.Default.Compare(a.Key, b.Key))
        );

    public int Count => sortedSet.Count;

    public Result<int> GetOffset(TKey key) {
        return sortedSet.TryGetValue((key, 0), out (TKey Key, int Offset) entry)
            ? entry.Offset
            : Result.Error(RhinoError.IndexKeyNotFound(key));
    }

    public Result Insert(TKey key, int offset) {
        return sortedSet.Add((key, offset))
            ? Result.Ok()
            : Result.Error(RhinoError.DuplicateKey(key));
    }

    public Result Delete(TKey key, int offset) {
        if (!sortedSet.TryGetValue((key, 0), out (TKey Key, int Offset) entry))
            return Result.Error(RhinoError.IndexKeyNotFound(key));

        if (entry.Offset != offset)
            return Result.Error(RhinoError.OffsetNotRegistered(key, offset));

        sortedSet.Remove(entry);
        return Result.Ok();
    }

    public List<int> Range(TKey from, TKey to) {
        if (Comparer<TKey>.Default.Compare(from, to) > 0) return [];

        var offsets = new List<int>();
        foreach ((TKey Key, int Offset) entry in sortedSet.GetViewBetween((from, 0), (to, 0)))
            offsets.Add(entry.Offset);

        return offsets;
    }
}
