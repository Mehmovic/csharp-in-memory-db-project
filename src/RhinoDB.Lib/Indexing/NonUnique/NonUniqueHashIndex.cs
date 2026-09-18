using RhinoDB.Lib.Settings;

namespace RhinoDB.Lib.Indexing;

public class NonUniqueHashIndex<TKey> where TKey : IEquatable<TKey> {
    private readonly Dictionary<TKey, HashSet<int>> hashMap = new Dictionary<TKey, HashSet<int>>();

    public OffsetList GetOffsets(TKey key) {
        if (!hashMap.TryGetValue(key, out var offsets)) return OffsetList.Empty();

        using var offsetBuilder = OffsetListBuilder.Create(Constants.OffsetBuilderInitialCapacity);
        foreach (var offset in offsets) offsetBuilder.Add(offset);
        return offsetBuilder.Build().Unwrap();
    }

    public void Insert(TKey key, int offset) {
        if (!hashMap.TryGetValue(key, out var offsets)) {
            offsets = [];
            hashMap[key] = offsets;
        }

        offsets.Add(offset);
    }

    public void Delete(TKey key, int offset) {
        var offsets = hashMap[key];
        offsets.Remove(offset);
        if (offsets.Count == 0) hashMap.Remove(key);
    }

    public OffsetList GetOffsetsIter() => ScanOffsets();

    public OffsetList GetOffsetsExcept(TKey key) {
        return ScanOffsets(FilterDescriptor.Exclude(key));
    }

    private OffsetList ScanOffsets(FilterDescriptor<TKey>? filterParam = null) {
        if (hashMap.Count == 0) return OffsetList.Empty();

        using var offsetBuilder = OffsetListBuilder.Create(Constants.OffsetBuilderInitialCapacity);

        foreach (var kvp in hashMap) {
            if (filterParam is { } filter && filter.MustExclude(kvp.Key)) continue;

            foreach (var offset in kvp.Value) {
                offsetBuilder.Add(offset);
            }
        }

        return offsetBuilder.Build().Unwrap();
    }
}
