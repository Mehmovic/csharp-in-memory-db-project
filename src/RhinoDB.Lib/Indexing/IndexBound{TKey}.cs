namespace RhinoDB.Lib.Indexing;

public readonly struct IndexBound<TKey> where TKey : IComparable<TKey> {
    public readonly bool IsBounded;
    public readonly bool IsInclusive;
    public readonly TKey Key;

    private IndexBound(bool isBounded, bool isInclusive, TKey key) {
        IsBounded = isBounded;
        IsInclusive = isInclusive;
        Key = key;
    }

    static public IndexBound<TKey> Unbounded => default;
    static public IndexBound<TKey> Inclusive(TKey key) => new IndexBound<TKey>(true, true, key);
    static public IndexBound<TKey> Exclusive(TKey key) => new IndexBound<TKey>(true, false, key);
}