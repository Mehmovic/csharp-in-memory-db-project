namespace RhinoDB.SchemaContracts;

public sealed class RevisionHistoryEntry {
    public int Generation { get; set; }
    public int Revision { get; set; }
}
