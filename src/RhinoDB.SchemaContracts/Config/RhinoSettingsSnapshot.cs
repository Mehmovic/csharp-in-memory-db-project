namespace RhinoDB.SchemaContracts;

// The compile-time view of rdbsettings.json that generators and analyzers act on. Value-equal, so incremental
// generators only re-run their downstream steps when one of these values actually changes.
public readonly struct RhinoSettingsSnapshot(ClientProtocolKind clientProtocol, NetworkTransportKind transport, string? parseError) : IEquatable<RhinoSettingsSnapshot> {
    static public readonly RhinoSettingsSnapshot Default = new RhinoSettingsSnapshot(ClientProtocolKind.Raw, NetworkTransportKind.WebSocket, null);

    public ClientProtocolKind ClientProtocol { get; } = clientProtocol;
    public NetworkTransportKind Transport { get; } = transport;
    public string? ParseError { get; } = parseError;

    static public RhinoSettingsSnapshot Read(string? json) {
        if (string.IsNullOrEmpty(json)) return Default;
        try {
            var config = GeneratorConfigLoader.ParseFull(json!);
            return new RhinoSettingsSnapshot(
                ClientProtocolParser.Parse(config.Generator.ClientProtocol),
                NetworkTransportParser.Parse(config.Network.Transport),
                null);
        } catch (Exception ex) {
            return new RhinoSettingsSnapshot(Default.ClientProtocol, Default.Transport, ex.Message);
        }
    }

    public bool Equals(RhinoSettingsSnapshot other) =>
        ClientProtocol == other.ClientProtocol && Transport == other.Transport && ParseError == other.ParseError;

    public override bool Equals(object? obj) => obj is RhinoSettingsSnapshot other && Equals(other);

    public override int GetHashCode() => ((int)ClientProtocol * 397) ^ ((int)Transport * 31) ^ (ParseError?.GetHashCode() ?? 0);
}
