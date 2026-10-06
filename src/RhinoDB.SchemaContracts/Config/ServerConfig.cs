using System.Text.Json.Serialization;

namespace RhinoDB.SchemaContracts;

public sealed class ServerConfig {
    public string Version { get; set; } = "0.0.0";
    public ArchiveRetentionConfig? ArchiveRetention { get; set; } = new ArchiveRetentionConfig();
    [JsonIgnore]
    public uint PackedVersion => ServerVersionParser.Parse(Version);
}
