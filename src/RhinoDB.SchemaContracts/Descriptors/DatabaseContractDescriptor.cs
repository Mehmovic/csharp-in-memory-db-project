namespace RhinoDB.SchemaContracts;

public sealed class DatabaseContractDescriptor {
    public List<DatabaseGenerationState> Databases { get; set; } = [];

    public Dictionary<string, int> TypeRevisions { get; set; } = [];

    public Dictionary<string, CustomTypeDescriptor> CustomTypes { get; set; } = [];
    public List<TableDescriptor> Tables { get; set; } = [];
}
