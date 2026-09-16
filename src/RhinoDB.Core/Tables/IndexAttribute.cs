namespace RhinoDB.Core.Tables;

[AttributeUsage(AttributeTargets.Parameter)]
public sealed class IndexAttribute
(
    IndexKind kind = IndexKind.BTree,
    Uniqueness uniqueness = Uniqueness.NonUnique
) : Attribute {
    public IndexKind Kind { get; } = kind;
    public Uniqueness Uniqueness { get; } = uniqueness;
    public string? Accessor { get; set; }
    public int Order { get; set; } = -1;
}
