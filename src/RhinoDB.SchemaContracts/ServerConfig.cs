namespace RhinoDB.SchemaContracts;

public sealed class ServerConfig {
    public string Version { get; set; } = "0.0.0";

    public uint PackedVersion => ServerVersionParser.Parse(Version);
}
