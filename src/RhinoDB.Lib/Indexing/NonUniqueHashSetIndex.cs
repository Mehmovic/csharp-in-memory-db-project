namespace RhinoDB.Lib.Indexing;

public class NonUniqueHashSetIndex<TKey>
    where TKey : notnull {
    private readonly Dictionary<TKey, HashSet<int>> hashMap = new Dictionary<TKey, HashSet<int>>();

    public Result Insert(TKey key, int offset) {
        if (!hashMap.TryGetValue(key, out var offsets)) {
            offsets = [];
            hashMap[key] = offsets;
        }

        offsets.Add(offset);
        return Result.Ok();
    }

    public Result Delete(TKey key, int offset) {
        if (!hashMap.TryGetValue(key, out var offsets))
            return Result.Error(RhinoError.IndexKeyNotFound(key));

        if (!offsets.Remove(offset))
            return Result.Error(RhinoError.OffsetNotRegistered(key, offset));

        if (offsets.Count == 0) hashMap.Remove(key);
        return Result.Ok();
    }

    public HashSet<int> GetOffsets(TKey key) {
        return hashMap.TryGetValue(key, out var offsets) ? offsets : [];
    }
}
