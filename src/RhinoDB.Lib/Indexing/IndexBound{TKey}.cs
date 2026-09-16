namespace RhinoDB.Lib.Indexing;

// One end of a key range. Positional by design: an inclusive bound reads as ">=" when
// it is used as the lower bound and "<=" when it is the upper one; an exclusive bound
// reads as ">" / "<" respectively. default(IndexBound<TKey>) is unbounded, so a caller
// can leave an end open without inventing a sentinel key - which no generic TKey has.
public readonly struct IndexBound<TKey> where TKey : IComparable<TKey> {
    public readonly bool IsBounded;
    public readonly bool IsInclusive;
    public readonly TKey Key;

    private IndexBound(bool isBounded, bool isInclusive, TKey key) {
        IsBounded = isBounded;
        IsInclusive = isInclusive;
        Key = key;
    }

    // This end of the range is open.
    static public IndexBound<TKey> Unbounded => default;

    // As a lower bound: key >= Key. As an upper bound: key <= Key.
    static public IndexBound<TKey> Inclusive(TKey key) => new IndexBound<TKey>(true, true, key);

    // As a lower bound: key > Key. As an upper bound: key < Key.
    static public IndexBound<TKey> Exclusive(TKey key) => new IndexBound<TKey>(true, false, key);
}