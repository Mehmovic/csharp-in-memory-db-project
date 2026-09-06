namespace RhinoDB.Lib.Indexing;

public class HashIndex<TKey> : IUniqueIndex<TKey>
    where TKey : notnull {
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
    
    public ICollection<int> Range(TKey from, TKey to) => throw new NotSupportedException();
}
