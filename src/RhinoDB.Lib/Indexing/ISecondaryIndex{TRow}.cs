namespace RhinoDB.Lib.Indexing;

public interface ISecondaryIndex<in TRow> where TRow : struct {
    Result CheckInsert(TRow row, int selfOffset);
    void Insert(TRow row, int offset);
    void Delete(TRow row, int offset);
}
