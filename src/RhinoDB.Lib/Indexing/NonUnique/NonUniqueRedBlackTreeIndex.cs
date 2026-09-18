using RhinoDB.Lib.Settings;

namespace RhinoDB.Lib.Indexing;

public class NonUniqueRedBlackTreeIndex<TKey> : OrderedIndex<TKey> where TKey : IComparable<TKey>, IEquatable<TKey> {
    private readonly SortedSet<(TKey Key, int Offset)> sortedSet = new SortedSet<(TKey Key, int Offset)>(
        Comparer<(TKey Key, int Offset)>.Create((a, b) => {
                var cmp = Comparer<TKey>.Default.Compare(a.Key, b.Key);
                return cmp != 0 ? cmp : a.Offset.CompareTo(b.Offset);
            }
        )
    );

    public OffsetList GetOffsets(TKey key)
        => ScanOffsets(IndexBound<TKey>.Inclusive(key), IndexBound<TKey>.Inclusive(key));

    public void Insert(TKey key, int offset) {
        sortedSet.Add((key, offset));
    }

    public void Delete(TKey key, int offset) {
        sortedSet.Remove((key, offset));
    }

    protected override OffsetList ScanOffsets(
        IndexBound<TKey> from,
        IndexBound<TKey> to,
        FilterDescriptor<TKey>? filterParam = null
    ) {
        if (sortedSet.Count == 0 || from.IsBounded && Comparer<TKey>.Default.Compare(from.Key, sortedSet.Max.Key) > 0)
            return OffsetList.Empty();

        using var offsetBuilder = OffsetListBuilder.Create(Constants.OffsetBuilderInitialCapacity);

        var view = from.IsBounded
            ? sortedSet.GetViewBetween((from.Key, int.MinValue), sortedSet.Max)
            : sortedSet;

        foreach (var entry in view) {
            if (filterParam is { } filter && filter.MustExclude(entry.Key)) continue;

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
