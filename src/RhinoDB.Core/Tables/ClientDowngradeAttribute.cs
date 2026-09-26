namespace RhinoDB.Core.Tables;

[AttributeUsage(AttributeTargets.Method)]
public sealed class ClientDowngradeAttribute(int toRevision) : Attribute {
    public int ToRevision { get; } = toRevision;
}
