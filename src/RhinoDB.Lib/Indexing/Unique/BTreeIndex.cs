namespace RhinoDB.Lib.Indexing;

public class BTreeIndex<TKey, TCmp> : OrderedIndex<TKey>
    where TKey : IComparable<TKey>, IEquatable<TKey>
    where TCmp : struct, IComparer<TKey> {
    private ChunkedKeyStore<TKey, TCmp> store;

    public int Count => store.Count;
    internal int ChunkCount => store.ChunkCount;

    public BTreeIndex(int chunkSize = 256) {
        store = new ChunkedKeyStore<TKey, TCmp>(chunkSize);
    }

    private BTreeIndex(ChunkedKeyStore<TKey, TCmp> built) {
        store = built;
    }

    public Result<int> GetOffset(TKey key) {
        if (!store.TryFind(key, out var chunkIdx, out var index)) return Result.Error(DbError.IndexKeyNotFound());
        return store.OffsetAt(chunkIdx, index);
    }

    public void Insert(TKey key, int offset) {
        store.InsertAt(store.LowerBound(key), key, offset);
    }

    public void Delete(TKey key) {
        if (!store.TryFind(key, out var chunkIdx, out var index)) return;
        store.RemoveAt(chunkIdx, index);
    }

    public void UpdateOffset(TKey key, int newOffset) {
        if (!store.TryFind(key, out var chunkIdx, out var index)) return;
        store.SetOffsetAt(chunkIdx, index, newOffset);
    }

    public void UpdateKey(TKey oldKey, TKey newKey, int newOffset) {
        Delete(oldKey);
        Insert(newKey, newOffset);
    }

    public void BulkLoadFrom(ReadOnlySpan<TKey> keys, ReadOnlySpan<int> offsets) {
        var built = ChunkedKeyStore<TKey, TCmp>.BuildUnique(keys, offsets, store.Capacity);
        store.Release();
        store = built;
    }

    protected override StackArrayPoolContainer<int> ScanOffsets(
        IndexBound<TKey> from,
        IndexBound<TKey> to,
        FilterDescriptor<TKey>? filterParam = null
    ) => store.Scan(from, to, filterParam);

    static public BTreeIndex<TKey, TCmp> BulkLoad(ReadOnlySpan<TKey> keys, ReadOnlySpan<int> offsets, int chunkSize = 256) =>
        new BTreeIndex<TKey, TCmp>(ChunkedKeyStore<TKey, TCmp>.BuildUnique(keys, offsets, chunkSize));
}
