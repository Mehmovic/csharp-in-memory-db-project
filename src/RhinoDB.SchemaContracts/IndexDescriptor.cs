namespace RhinoDB.SchemaContracts;

public sealed class IndexDescriptor {
    public string Accessor { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Uniqueness { get; set; } = "";
    public List<string> FieldPaths { get; set; } = [];
}
