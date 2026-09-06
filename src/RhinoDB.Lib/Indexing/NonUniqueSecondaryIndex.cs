namespace RhinoDB.Lib.Indexing;

public class NonUniqueSecondaryIndex<TRow, TKey>(INonUniqueHash<TKey> idx, Func<TRow, TKey> keySelector)
    : ISecondaryIndex<TRow>
    where TKey : notnull
    where TRow : struct {
    public  Result CheckInsert(TRow row, int selfOffset) {
        return Result.Ok();
    }

    public void Insert(TRow row, int offset) {
        idx.Insert(keySelector(row), offset);
    }

    public void Delete(TRow row, int offset) {
        idx.Delete(keySelector(row), offset);
    }
}
