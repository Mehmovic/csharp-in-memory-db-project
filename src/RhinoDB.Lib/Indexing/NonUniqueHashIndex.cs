using RhinoDB.Lib.Storage;

namespace RhinoDB.Lib.Indexing;

public class NonUniqueHashIndex<TKey, TRow>(DenseArray<TRow> storage)
    where TKey : notnull
    where TRow : struct {
    private readonly Dictionary<TKey, List<int>> hashMap = new Dictionary<TKey, List<int>>();

    public Result Register(TKey key, int offset) {
        if (!hashMap.TryGetValue(key, out var offsets)) {
            offsets = [];
            hashMap[key] = offsets;
        }

        offsets.Add(offset);
        return Result.Ok();
    }

    public Result Deregister(TKey key, int offset) {
        var offsets = hashMap[key];
        var position = offsets.IndexOf(offset);

        var lastIndex = offsets.Count - 1;
        offsets[position] = offsets[lastIndex];
        offsets.RemoveAt(lastIndex);

        if (offsets.Count == 0) hashMap.Remove(key);
        return Result.Ok();
    }

    public List<TRow> Get(TKey key) {
        if (!hashMap.TryGetValue(key, out var offsets)) return [];

        var rows = new List<TRow>(offsets.Count);
        foreach (var offset in offsets)
            rows.Add(storage.Get(offset));

        return rows;
    }
}
