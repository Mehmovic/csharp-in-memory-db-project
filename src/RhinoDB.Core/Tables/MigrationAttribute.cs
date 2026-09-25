namespace RhinoDB.Core.Tables;

[AttributeUsage(AttributeTargets.Method)]
public sealed class MigrationAttribute(int fromRevision) : Attribute {
    public int FromRevision { get; } = fromRevision;
}
