using RhinoDB.Lib.Settings;

namespace RhinoDB.Lib.Indexing;

public class RedBlackTreeIndex<TKey> : OrderedIndex<TKey>
    where TKey : IComparable<TKey> {
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

    protected override OffsetList Scan(IndexBound<TKey> from, IndexBound<TKey> to, (bool filter, TKey key)? filterCondition = null) {
        if (sortedSet.Count == 0 || from.IsBounded && Comparer<TKey>.Default.Compare(from.Key, sortedSet.Max.Key) > 0)
            return OffsetList.Empty();

        using var offsetBuilder = OffsetListBuilder.Create(Constants.OffsetBuilderInitialCapacity);

        var view = from.IsBounded
            ? sortedSet.GetViewBetween((from.Key, 0), sortedSet.Max)
            : sortedSet;

        foreach ((TKey Key, int Offset) entry in view) {
            if (filterCondition is { filter: true } filter && Comparer<TKey>.Default.Compare(entry.Key, filter.key) == 0) continue;

            if (from is { IsBounded: true, IsInclusive: false } && Comparer<TKey>.Default.Compare(entry.Key, from.Key) == 0) continue;

            if (to.IsBounded) {
                var cmp = Comparer<TKey>.Default.Compare(entry.Key, to.Key);
                if (cmp > 0 || (cmp == 0 && !to.IsInclusive)) break;
            }

            offsetBuilder.Add(entry.Offset);
        }

        return offsetBuilder.Build().Unwrap();
    }
}
