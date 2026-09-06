namespace RhinoDB.Lib.Indexing;

public interface INonUniqueHash<in TKey> where TKey : notnull {
    void Insert(TKey key, int offset);
    void Delete(TKey key, int offset);
    ICollection<int> GetOffsets(TKey key);
}
