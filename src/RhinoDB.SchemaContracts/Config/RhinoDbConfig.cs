namespace RhinoDB.SchemaContracts;

public sealed class RhinoDbConfig {
    public GeneratorConfig Generator { get; set; } = new GeneratorConfig();
    public ServerConfig Server { get; set; } = new ServerConfig();
    public HostConfig Host { get; set; } = new HostConfig();
}
