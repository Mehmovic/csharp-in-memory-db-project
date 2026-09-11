namespace RhinoDB.Lib.Tables;

public enum PendingLookup {
    NotStaged,
    Present,
    Deleted,
}

public sealed class ChangeSet<TKey, TRow>(Func<TRow, TKey> selector)
    where TKey : IEquatable<TKey>, IComparable<TKey>
    where TRow : struct {
    private readonly List<Change<TKey, TRow>> changes = [];

    public IReadOnlyList<Change<TKey, TRow>> Changes => changes;

    public void Insert(TRow record) => changes.Add(new Change<TKey, TRow>(ChangeKind.Insert, selector(record), record));

    public void Update(TKey id, TRow newRecord) => changes.Add(new Change<TKey, TRow>(ChangeKind.Update, id, newRecord));

    public void Delete(TKey id, TRow currentRow) => changes.Add(new Change<TKey, TRow>(ChangeKind.Delete, id, currentRow));

    public void Clear() => changes.Clear();

    public PendingLookup TryGetPending(TKey id, out TRow row) {
        for (var i = changes.Count - 1; i >= 0; i--) {
            if (!changes[i].Key.Equals(id)) continue;
            if (changes[i].Kind == ChangeKind.Delete) {
                row = default;
                return PendingLookup.Deleted;
            }
            row = changes[i].Row;
            return PendingLookup.Present;
        }
        row = default;
        return PendingLookup.NotStaged;
    }
}
