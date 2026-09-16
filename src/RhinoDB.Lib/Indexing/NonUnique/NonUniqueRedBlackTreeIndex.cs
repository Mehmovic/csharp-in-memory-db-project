namespace RhinoDB.Lib.Indexing;

public class NonUniqueRedBlackTreeIndex<TKey>
    where TKey : notnull {
    private readonly SortedSet<(TKey Key, int Offset)> sortedSet = new SortedSet<(TKey Key, int Offset)>(
        Comparer<(TKey Key, int Offset)>.Create((a, b) => {
                var cmp = Comparer<TKey>.Default.Compare(a.Key, b.Key);
                return cmp != 0 ? cmp : a.Offset.CompareTo(b.Offset);
            }
        )
    );
    
    public void GetOffsets(TKey key, ICollection<int> into) => Range(key, key, into);

    public void Insert(TKey key, int offset) {
        sortedSet.Add((key, offset));
    }

    public void Delete(TKey key, int offset) {
        sortedSet.Remove((key, offset));
    }

    public void Range(TKey from, TKey to, ICollection<int> into) {
        if (Comparer<TKey>.Default.Compare(from, to) > 0) return;

        foreach ((TKey Key, int Offset) entry in sortedSet.GetViewBetween((from, int.MinValue), (to, int.MaxValue)))
            into.Add(entry.Offset);
    }
}
