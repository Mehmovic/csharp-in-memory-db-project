namespace RhinoDB.Lib.Indexing;

public class NonUniqueHashIndex<TKey>
    where TKey : notnull {
    private readonly Dictionary<TKey, HashSet<int>> hashMap = new Dictionary<TKey, HashSet<int>>();

    public void GetOffsets(TKey key, ICollection<int> into) {
        if (!hashMap.TryGetValue(key, out var offsets)) return;
        
        foreach (var offset in offsets) into.Add(offset);
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
}
