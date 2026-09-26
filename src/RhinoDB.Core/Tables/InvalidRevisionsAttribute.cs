namespace RhinoDB.Core.Tables;

[AttributeUsage(AttributeTargets.Struct)]
public sealed class InvalidRevisionsAttribute : Attribute {
    public int[]? Revisions { get; set; }
}
