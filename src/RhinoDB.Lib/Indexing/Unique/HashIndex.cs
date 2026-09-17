using RhinoDB.Lib.Settings;

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
    
    public OffsetList Iter() => Scan();

    public OffsetList Filter(TKey key) {
        return Scan(FilterDescriptor.Include(key));
    }

    public OffsetList Except(TKey key) {
        return Scan(FilterDescriptor.Exclude(key));
    }

    private OffsetList Scan(FilterDescriptor<TKey>? filterParam = null) {
        if (hashMap.Count == 0) return OffsetList.Empty();

        using var offsetBuilder = OffsetListBuilder.Create(Constants.OffsetBuilderInitialCapacity);

        foreach (var kvp in hashMap) {
            if (filterParam is { } filter && filter.MustExclude(kvp.Key)) continue;

            offsetBuilder.Add(kvp.Value);
        }

        return offsetBuilder.Build().Unwrap();
    }
}
