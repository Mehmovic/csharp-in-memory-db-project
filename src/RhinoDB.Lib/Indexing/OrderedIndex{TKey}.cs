namespace RhinoDB.Lib.Indexing;

public abstract class OrderedIndex<TKey> where TKey : IComparable<TKey>, IEquatable<TKey> {
    // [from, to]
    public StackArrayPoolContainer<int> GetOffsetsRange(TKey from, TKey to)
        => ScanOffsets(IndexBound<TKey>.Inclusive(from), IndexBound<TKey>.Inclusive(to));

    // (from, ...)
    public StackArrayPoolContainer<int> GetOffsetsGt(TKey from)
        => ScanOffsets(IndexBound<TKey>.Exclusive(from), IndexBound<TKey>.Unbounded);

    // [from, ...)
    public StackArrayPoolContainer<int> GetOffsetsGte(TKey from)
        => ScanOffsets(IndexBound<TKey>.Inclusive(from), IndexBound<TKey>.Unbounded);

    // (..., to)
    public StackArrayPoolContainer<int> GetOffsetsLt(TKey to)
        => ScanOffsets(IndexBound<TKey>.Unbounded, IndexBound<TKey>.Exclusive(to));

    // (..., to]
    public StackArrayPoolContainer<int> GetOffsetsLte(TKey to)
        => ScanOffsets(IndexBound<TKey>.Unbounded, IndexBound<TKey>.Inclusive(to));

    // Everything, in ascending key order.
    public StackArrayPoolContainer<int> GetOffsetsIter()
        => ScanOffsets(IndexBound<TKey>.Unbounded, IndexBound<TKey>.Unbounded);

    public StackArrayPoolContainer<int> GetOffsetsExcept(TKey key)
        => ScanOffsets(IndexBound<TKey>.Unbounded, IndexBound<TKey>.Unbounded, FilterDescriptor.Exclude(key));

    protected abstract StackArrayPoolContainer<int> ScanOffsets(
        IndexBound<TKey> from,
        IndexBound<TKey> to,
        FilterDescriptor<TKey>? filter = null
    );
}
