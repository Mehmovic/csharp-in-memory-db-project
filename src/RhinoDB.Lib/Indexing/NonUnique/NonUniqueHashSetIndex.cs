namespace RhinoDB.Lib.Indexing;

public class NonUniqueHashSetIndex<TKey>
    where TKey : notnull {
    private readonly Dictionary<TKey, HashSet<int>> hashMap = new Dictionary<TKey, HashSet<int>>();

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

    public HashSet<int> GetOffsets(TKey key) {
        return hashMap.TryGetValue(key, out var offsets) ? offsets : [];
    }
}
