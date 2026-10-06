namespace RhinoDB.SchemaContracts;

public sealed class RhinoDbConfig {
    public GeneratorConfig Generator { get; set; } = new GeneratorConfig();
    public ServerConfig Server { get; set; } = new ServerConfig();
    public HostConfig Host { get; set; } = new HostConfig();
    public NetworkConfig Network { get; set; } = new NetworkConfig();
    public DurabilityConfig Durability { get; set; } = new DurabilityConfig();
    public PrefsConfig Prefs { get; set; } = new PrefsConfig();
}
