namespace RhinoDB.Lib.Tables;

public interface IRowMutator<TRow> where TRow : struct {
    void Update(TRow original, TRow newRow);
    void Delete(TRow row);
    TRow WithSamePrimaryKey(TRow original, TRow newRow);
}
