namespace RhinoDB.Lib.Tables;

public enum ChangeKind : byte {
    Insert,
    Update,
    Delete,
}

public readonly struct Change<TKey, TRow>(ChangeKind kind, TKey key, TRow row)
    where TKey : IEquatable<TKey>, IComparable<TKey>
    where TRow : struct {
    public ChangeKind Kind { get; } = kind;
    public TKey Key { get; } = key;
    public TRow Row { get; } = row;
}
