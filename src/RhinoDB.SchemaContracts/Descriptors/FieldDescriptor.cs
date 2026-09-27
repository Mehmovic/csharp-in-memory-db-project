namespace RhinoDB.SchemaContracts;

public sealed class FieldDescriptor {
    public string Path { get; set; } = "";
    public string TypeFullName { get; set; } = "";
    public RowFieldKind Kind { get; set; }
}
