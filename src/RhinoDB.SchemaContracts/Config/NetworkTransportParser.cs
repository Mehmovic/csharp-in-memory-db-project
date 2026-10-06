namespace RhinoDB.SchemaContracts;

static public class NetworkTransportParser {
    static public NetworkTransportKind Parse(string value) => value.ToLowerInvariant() switch {
        "websocket" or "ws" => NetworkTransportKind.WebSocket,
        _ => throw new GeneratorConfigException($"Unrecognized Network.Transport '{value}' - the only valid value is 'WebSocket'."),
    };
}
