namespace RhinoDB.Lib.Indexing;

public class HashIndex<TKey>
    where TKey : notnull {
    private readonly Dictionary<TKey, int> hashMap = new Dictionary<TKey, int>();

    public int Count => hashMap.Count;

    public Result<int> GetOffset(TKey key) {
        return hashMap.TryGetValue(key, out var offset)
            ? offset
            : Result.Error(new IndexKeyNotFoundException(key));
    }

    public Result Insert(TKey key, int offset) {
        return hashMap.TryAdd(key, offset)
            ? Result.Ok()
            : Result.Error(new DuplicateKeyException(key));
    }

    public Result Delete(TKey key, int offset) {
        if (!hashMap.TryGetValue(key, out var storedOffset))
            return Result.Error(new IndexKeyNotFoundException(key));

        if (storedOffset != offset)
            return Result.Error(new OffsetNotRegisteredException(key, offset));

        hashMap.Remove(key);
        return Result.Ok();
    }
}
