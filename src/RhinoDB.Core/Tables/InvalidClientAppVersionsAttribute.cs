namespace RhinoDB.Core.Tables;

[AttributeUsage(AttributeTargets.Assembly)]
public sealed class InvalidClientAppVersionsAttribute : Attribute {
    public uint[]? Whitelist { get; set; }
    public uint[]? LessThanOrEqualTo { get; set; }
    public uint[]? NotEqualTo { get; set; }
}
