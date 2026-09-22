namespace RhinoDB.SchemaContracts;

public sealed class CustomTypeDescriptor {
    public string FullName { get; set; } = "";
    public List<FieldDescriptor> Fields { get; set; } = [];
}
