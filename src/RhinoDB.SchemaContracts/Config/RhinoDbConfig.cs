namespace RhinoDB.SchemaContracts;

public sealed class RhinoDbConfig {
    public GeneratorConfig Generator { get; set; } = new();
    public ServerConfig Server { get; set; } = new();
    public HostConfig Host { get; set; } = new();
}
