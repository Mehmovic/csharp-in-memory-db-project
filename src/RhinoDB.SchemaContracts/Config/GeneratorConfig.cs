namespace RhinoDB.SchemaContracts;

public sealed class GeneratorConfig {
    public string SourceParentDirectory { get; set; } = "RhinoContracts";
    public string TargetParentDirectory { get; set; } = "RhinoDB";
    public string SchemaDescriptorPath { get; set; } = "RhinoContracts/Descriptor.json";

    // Deliberately NOT under RhinoContracts, it needs to be compiled
    public string MigrationsOutputDirectory { get; set; } = "Migrations";

    public string ClientProtocol { get; set; } = "Raw";

    // When true, RhinoDB.PreBuild.targets skips its automatic BeforeTargets="CoreCompile" expansion -
    // the developer runs `rhinodb contract generate`/`clean` manually instead, protobuf-style.
    public bool DisableContractAutoPreBuild { get; set; } = true;
}
