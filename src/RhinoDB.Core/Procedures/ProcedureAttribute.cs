namespace RhinoDB.Core.Procedures;

[AttributeUsage(AttributeTargets.Method)]
public sealed class ProcedureAttribute : Attribute {
    public string? Name { get; set; }
}
