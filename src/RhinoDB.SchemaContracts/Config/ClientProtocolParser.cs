namespace RhinoDB.SchemaContracts;

static public class ClientProtocolParser {
    static public ClientProtocolKind Parse(string value) => value switch {
        "Raw" => ClientProtocolKind.Raw,
        "VersionedMemoryPack" => ClientProtocolKind.VersionedMemoryPack,
        "MessagePack" => ClientProtocolKind.MessagePack,
        _ => throw new GeneratorConfigException(
            $"Unrecognized ClientProtocol '{value}' - valid values are 'Raw', 'VersionedMemoryPack', 'MessagePack'."),
    };
}
