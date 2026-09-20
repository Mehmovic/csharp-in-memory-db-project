namespace RhinoDB.Core.Tables;

[AttributeUsage(AttributeTargets.Parameter)]
public sealed class PackIdAttribute(int value) : Attribute {
    public int Value { get; } = value;
}
