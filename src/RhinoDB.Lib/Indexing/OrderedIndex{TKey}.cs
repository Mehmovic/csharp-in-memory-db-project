namespace RhinoDB.Lib.Indexing;

public abstract class OrderedIndex<TKey> where TKey : IComparable<TKey> {
    // [from, to]
    public OffsetList Range(TKey from, TKey to)
        => Scan(IndexBound<TKey>.Inclusive(from), IndexBound<TKey>.Inclusive(to));

    // (from, ...)
    public OffsetList Gt(TKey from)
        => Scan(IndexBound<TKey>.Exclusive(from), IndexBound<TKey>.Unbounded);

    // [from, ...)
    public OffsetList Gte(TKey from)
        => Scan(IndexBound<TKey>.Inclusive(from), IndexBound<TKey>.Unbounded);

    // (..., to)
    public OffsetList Lt(TKey to)
        => Scan(IndexBound<TKey>.Unbounded, IndexBound<TKey>.Exclusive(to));

    // (..., to]
    public OffsetList Lte(TKey to)
        => Scan(IndexBound<TKey>.Unbounded, IndexBound<TKey>.Inclusive(to));

    // Everything, in ascending key order.
    public OffsetList Iter()
        => Scan(IndexBound<TKey>.Unbounded, IndexBound<TKey>.Unbounded);
    
    public OffsetList Filter(TKey key)
        => Scan(IndexBound<TKey>.Unbounded, IndexBound<TKey>.Unbounded, (true, key));

    protected abstract OffsetList Scan(IndexBound<TKey> from, IndexBound<TKey> to, (bool filter, TKey key)? filterCondition = null);
}