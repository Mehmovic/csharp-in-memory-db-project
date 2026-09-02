namespace RhinoDB.Lib.Indexing;

public class HashIndex<TKey>
    where TKey : notnull {
    private readonly Dictionary<TKey, int> hashMap = new Dictionary<TKey, int>();

    public int Count => hashMap.Count;

    public Result<int> GetOffset(TKey key) {
        return hashMap.TryGetValue(key, out var offset)
            ? offset
            : Result.Error(RhinoError.IndexKeyNotFound(key));
    }

    public void Insert(TKey key, int offset) {
        hashMap.Add(key, offset);
    }

    public void Delete(TKey key) {
        hashMap.Remove(key);
    }
}
