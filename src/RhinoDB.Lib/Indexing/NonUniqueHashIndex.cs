namespace RhinoDB.Lib.Indexing;

public class NonUniqueHashIndex<TKey>
    where TKey : notnull {
    private readonly Dictionary<TKey, List<int>> hashMap = new Dictionary<TKey, List<int>>();

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

        var position = offsets.IndexOf(offset);
        if (position == -1)
            return Result.Error(RhinoError.OffsetNotRegistered(key, offset));

        var lastIndex = offsets.Count - 1;
        offsets[position] = offsets[lastIndex];
        offsets.RemoveAt(lastIndex);

        if (offsets.Count == 0) hashMap.Remove(key);
        return Result.Ok();
    }

    public List<int> GetOffsets(TKey key) {
        return hashMap.TryGetValue(key, out var offsets) ? offsets : [];
    }
}
