namespace RhinoDB.Lib.Indexing;

public interface IUniqueIndex<in TKey> where TKey : notnull {
    int Count { get; }

    Result<int> GetOffset(TKey key);

    void Insert(TKey key, int offset);

    void Delete(TKey key);

    ICollection<int> Range(TKey from, TKey to);
}
