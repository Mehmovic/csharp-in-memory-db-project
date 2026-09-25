namespace RhinoDB.Core.Tables;

[AttributeUsage(AttributeTargets.Struct)]
public sealed class FrozenSchemaAttribute(int revision) : Attribute {
    public int Revision { get; } = revision;
}
