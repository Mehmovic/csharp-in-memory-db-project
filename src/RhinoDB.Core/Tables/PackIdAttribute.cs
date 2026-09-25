namespace RhinoDB.Core.Tables;

[AttributeUsage(AttributeTargets.Parameter)]
public sealed class PackIdAttribute(byte value) : Attribute {
    public byte Value { get; } = value;
}
