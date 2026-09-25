namespace RhinoDB.PreBuild;

public sealed class GeneratorConfig {
    public string SourceParentDirectory { get; set; } = "RhinoContracts";
    public string TargetParentDirectory { get; set; } = "RhinoDB";
    public string SchemaDescriptorPath { get; set; } = "RhinoContracts/Descriptor.json";

    // Deliberately NOT under RhinoContracts, it needs to be compiled
    public string MigrationsOutputDirectory { get; set; } = "Migrations";
}
