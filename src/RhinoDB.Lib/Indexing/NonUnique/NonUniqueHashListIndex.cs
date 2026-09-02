namespace RhinoDB.Lib.Indexing;

public class NonUniqueHashIndex<TKey>
    where TKey : notnull {
    private readonly Dictionary<TKey, List<int>> hashMap = new Dictionary<TKey, List<int>>();

    public void Insert(TKey key, int offset) {
        if (!hashMap.TryGetValue(key, out var offsets)) {
            offsets = [];
            hashMap[key] = offsets;
        }

        offsets.Add(offset);
    }

    public void Delete(TKey key, int offset) {
        var offsets = hashMap[key];
        var position = offsets.IndexOf(offset);
        var lastIndex = offsets.Count - 1;
        offsets[position] = offsets[lastIndex];
        offsets.RemoveAt(lastIndex);

        if (offsets.Count == 0) hashMap.Remove(key);
    }

    public List<int> GetOffsets(TKey key) {
        return hashMap.TryGetValue(key, out var offsets) ? offsets : [];
    }
}
