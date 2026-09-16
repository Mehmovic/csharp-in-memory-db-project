namespace RhinoDB.Lib.Indexing;

// Shared surface of the index families that keep keys ordered - BTreeIndex and
// RedBlackTreeIndex and their non-unique counterparts. Hash-backed indexes cannot
// produce an ordered scan and deliberately do not derive from this, which is what keeps
// a range-shaped method off a base they would have to throw NotSupportedException from.
public abstract class OrderedIndex<TKey> where TKey : IComparable<TKey> {
    // [from, to]
    public int Range(TKey from, TKey to, ICollection<int> into)
        => Scan(IndexBound<TKey>.Inclusive(from), IndexBound<TKey>.Inclusive(to), into);

    // (from, ...)
    public int Gt(TKey from, ICollection<int> into)
        => Scan(IndexBound<TKey>.Exclusive(from), IndexBound<TKey>.Unbounded, into);

    // [from, ...)
    public int Gte(TKey from, ICollection<int> into)
        => Scan(IndexBound<TKey>.Inclusive(from), IndexBound<TKey>.Unbounded, into);

    // (..., to)
    public int Lt(TKey to, ICollection<int> into)
        => Scan(IndexBound<TKey>.Unbounded, IndexBound<TKey>.Exclusive(to), into);

    // (..., to]
    public int Lte(TKey to, ICollection<int> into)
        => Scan(IndexBound<TKey>.Unbounded, IndexBound<TKey>.Inclusive(to), into);

    // Everything, in ascending key order.
    public int Iter(ICollection<int> into)
        => Scan(IndexBound<TKey>.Unbounded, IndexBound<TKey>.Unbounded, into);

    // The single method each index implements - the six above are one-line wrappers, so
    // the open/closed semantics cannot drift per implementation. Returns the number of
    // offsets appended to 'into'.
    protected abstract int Scan(IndexBound<TKey> from, IndexBound<TKey> to, ICollection<int> into);
}