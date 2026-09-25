namespace RhinoDB.SchemaContracts;

public sealed class DatabaseGenerationState {
    public string FullName { get; set; } = "";
    public int Generation { get; set; }
    public List<int> InvalidGenerations { get; set; } = [];

    public int RetainedFromGeneration { get; set; }
}
