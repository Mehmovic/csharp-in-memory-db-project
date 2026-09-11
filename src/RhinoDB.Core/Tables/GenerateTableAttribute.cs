namespace RhinoDB.Core.Tables;

[AttributeUsage(AttributeTargets.Struct)]
public sealed class GenerateTableAttribute(TableKind kind, Type database) : Attribute {
    public TableKind Kind { get; } = kind;
    public Type Database { get; } = database;
}
