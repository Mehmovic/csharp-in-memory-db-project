namespace RhinoDB.Lib.Indexing;

public abstract class OrderedIndex<TKey> where TKey : IComparable<TKey>, IEquatable<TKey> {
    // [from, to]
    public OffsetList GetOffsetsRange(TKey from, TKey to)
        => ScanOffsets(IndexBound<TKey>.Inclusive(from), IndexBound<TKey>.Inclusive(to));

    // (from, ...)
    public OffsetList GetOffsetsGt(TKey from)
        => ScanOffsets(IndexBound<TKey>.Exclusive(from), IndexBound<TKey>.Unbounded);

    // [from, ...)
    public OffsetList GetOffsetsGte(TKey from)
        => ScanOffsets(IndexBound<TKey>.Inclusive(from), IndexBound<TKey>.Unbounded);

    // (..., to)
    public OffsetList GetOffsetsLt(TKey to)
        => ScanOffsets(IndexBound<TKey>.Unbounded, IndexBound<TKey>.Exclusive(to));

    // (..., to]
    public OffsetList GetOffsetsLte(TKey to)
        => ScanOffsets(IndexBound<TKey>.Unbounded, IndexBound<TKey>.Inclusive(to));

    // Everything, in ascending key order.
    public OffsetList GetOffsetsIter()
        => ScanOffsets(IndexBound<TKey>.Unbounded, IndexBound<TKey>.Unbounded);

    public OffsetList GetOffsetsExcept(TKey key)
        => ScanOffsets(IndexBound<TKey>.Unbounded, IndexBound<TKey>.Unbounded, FilterDescriptor.Exclude(key));

    protected abstract OffsetList ScanOffsets(
        IndexBound<TKey> from,
        IndexBound<TKey> to,
        FilterDescriptor<TKey>? filter = null
    );
}
