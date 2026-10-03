namespace RhinoDB.Lib.Indexing;

public class NonUniqueBTreeIndex<TKey, TCmp> : OrderedIndex<TKey>
    where TKey : IComparable<TKey>, IEquatable<TKey>
    where TCmp : struct, IComparer<TKey> {
    private ChunkedKeyStore<TKey, TCmp> store;

    public int Count => store.Count;
    internal int ChunkCount => store.ChunkCount;

    public NonUniqueBTreeIndex(int chunkSize = 256) {
        store = new ChunkedKeyStore<TKey, TCmp>(chunkSize);
    }

    private NonUniqueBTreeIndex(ChunkedKeyStore<TKey, TCmp> built) {
        store = built;
    }

    public StackArrayPoolContainer<int> GetOffsets(TKey key)
        => store.Scan(IndexBound<TKey>.Inclusive(key), IndexBound<TKey>.Inclusive(key), null);

    public void Insert(TKey key, int offset) {
        store.InsertAt(store.LowerBoundPair(key, offset), key, offset);
    }

    public void Delete(TKey key, int offset) {
        if (!store.TryFindPair(key, offset, out var chunkIdx, out var index)) return;
        store.RemoveAt(chunkIdx, index);
    }

    public void BulkLoadFrom(ReadOnlySpan<TKey> keys, ReadOnlySpan<int> offsets) {
        var built = ChunkedKeyStore<TKey, TCmp>.BuildNonUnique(keys, offsets, store.Capacity);
        store.Release();
        store = built;
    }

    protected override StackArrayPoolContainer<int> ScanOffsets(
        IndexBound<TKey> from,
        IndexBound<TKey> to,
        FilterDescriptor<TKey>? filterParam = null
    ) => store.Scan(from, to, filterParam);

    static public NonUniqueBTreeIndex<TKey, TCmp> BulkLoad(ReadOnlySpan<TKey> keys, ReadOnlySpan<int> offsets, int chunkSize = 256) =>
        new NonUniqueBTreeIndex<TKey, TCmp>(ChunkedKeyStore<TKey, TCmp>.BuildNonUnique(keys, offsets, chunkSize));
}
