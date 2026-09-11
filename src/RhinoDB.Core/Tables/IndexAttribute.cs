namespace RhinoDB.Core.Tables;

[AttributeUsage(AttributeTargets.Parameter)]
public sealed class IndexAttribute(IndexKind kind, Uniqueness uniqueness = Uniqueness.NonUnique) : Attribute {
    public IndexKind Kind { get; } = kind;
    public Uniqueness Uniqueness { get; } = uniqueness;
}
