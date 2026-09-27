namespace RhinoDB.SchemaContracts;

public sealed class TableDescriptor {
    public string DatabaseFullName { get; set; } = "";

    public string Accessor { get; set; } = "";
    public string RowTypeFullName { get; set; } = "";
    public string Kind { get; set; } = "";
    public uint TableIdHash { get; set; }

    public int Revision { get; set; }

    public FieldDescriptor PrimaryKey { get; set; } = new();

    public List<FieldDescriptor> Fields { get; set; } = [];

    public List<IndexDescriptor> Indexes { get; set; } = [];

    public List<RevisionHistoryEntry> RevisionHistory { get; set; } = [];

    public int? RemovedAtGeneration { get; set; }
}
