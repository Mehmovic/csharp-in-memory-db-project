namespace RhinoDB.Lib.Indexing;

public class UniqueSecondaryIndex<TRow, TKey>(HashIndex<TKey> idx, Func<TRow, TKey> keySelector)
    : ISecondaryIndex<TRow>
    where TKey : notnull
    where TRow : struct {
    public Result CheckInsert(TRow row, int selfOffset) {
        var result = idx.GetOffset(keySelector(row));
        if (result.IsError()) return Result.Ok();
        return result.Unwrap() == selfOffset ? Result.Ok() : Result.Error(DbError.DuplicateKey());
    }

    public void Insert(TRow row, int offset) {
        idx.Insert(keySelector(row), offset);
    }

    public void Delete(TRow row, int offset) {
        idx.Delete(keySelector(row));
    }
}
