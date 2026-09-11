namespace RhinoDB.Core.Tables;

[AttributeUsage(AttributeTargets.Parameter)]
public sealed class PrimaryKeyAttribute(IndexKind kind = IndexKind.Hash) : Attribute {
    public IndexKind Kind { get; } = kind;
}
