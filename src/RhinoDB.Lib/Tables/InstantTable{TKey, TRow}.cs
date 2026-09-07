using RhinoDB.Lib.Indexing;

namespace RhinoDB.Lib.Tables;

public class InstantTable<TKey, TRow>(int chunkSize, IUniqueIndex<TKey> primary, Func<TRow, TKey> selector)
    where TKey : IEquatable<TKey>, IComparable<TKey>
    where TRow : struct {
    private readonly Table<TKey, TRow> table = new(chunkSize, primary, selector);

    public int Count => table.Count;

    public void Register(ISecondaryIndex<TRow> secondaryIndex) => table.Register(secondaryIndex);

    public TRow GetByOffset(int offset) => table.GetByOffset(offset);

    public Result<TRow> Get(TKey id) => table.Get(id);

    public Result Insert(TRow record) => table.Insert(record);

    public Result Delete(TKey id) => table.Delete(id);

    public Result Update(TKey id, TRow newRecord) => table.Update(id, newRecord);
}
