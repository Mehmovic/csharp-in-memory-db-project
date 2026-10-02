namespace RhinoDB.Lib.Indexing;

public class HashIndex<TKey> where TKey : IEquatable<TKey> {
    private readonly Dictionary<TKey, int> hashMap = new Dictionary<TKey, int>();

    public int Count => hashMap.Count;

    public Result<int> GetOffset(TKey key) {
        return hashMap.TryGetValue(key, out var offset)
            ? offset
            : Result.Error(DbError.IndexKeyNotFound());
    }

    public void Insert(TKey key, int offset) {
        hashMap.Add(key, offset);
    }

    public void Delete(TKey key) {
        hashMap.Remove(key);
    }
    
    public StackArrayPoolContainer<int> GetOffsetsIter() => ScanOffsets();

    public StackArrayPoolContainer<int> GetOffsetsExcept(TKey key) {
        return ScanOffsets(FilterDescriptor.Exclude(key));
    }

    private StackArrayPoolContainer<int> ScanOffsets(FilterDescriptor<TKey>? filterParam = null) {
        if (hashMap.Count == 0) return StackArrayPoolContainer<int>.Empty();

        using var offsetBuilder = StackArrayPoolContainerBuilder<int>.Create(hashMap.Count);

        foreach (var kvp in hashMap) {
            if (filterParam is { } filter && filter.MustExclude(kvp.Key)) continue;

            offsetBuilder.Add(kvp.Value);
        }

        return offsetBuilder.Build().Unwrap();
    }
}
