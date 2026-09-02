namespace RhinoDB.Lib.Indexing;

public class NonUniqueOrderedIndex<TKey>
    where TKey : notnull {
    private readonly SortedSet<(TKey Key, int Offset)> sortedSet = new SortedSet<(TKey Key, int Offset)>(
        Comparer<(TKey Key, int Offset)>.Create((a, b) => {
                var cmp = Comparer<TKey>.Default.Compare(a.Key, b.Key);
                return cmp != 0 ? cmp : a.Offset.CompareTo(b.Offset);
            }
        )
    );

    public void Insert(TKey key, int offset) {
        sortedSet.Add((key, offset));
    }

    public void Delete(TKey key, int offset) {
        sortedSet.Remove((key, offset));
    }

    public List<int> GetOffsets(TKey key) => Range(key, key);

    public List<int> Range(TKey from, TKey to) {
        if (Comparer<TKey>.Default.Compare(from, to) > 0) return [];

        var offsets = new List<int>();
        foreach ((TKey Key, int Offset) entry in sortedSet.GetViewBetween((from, int.MinValue), (to, int.MaxValue)))
            offsets.Add(entry.Offset);

        return offsets;
    }
}
