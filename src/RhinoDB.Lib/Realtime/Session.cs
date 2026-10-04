namespace RhinoDB.Lib.Realtime;

public readonly record struct ConnectionId(Guid Value) {
    static public ConnectionId NewId() => new ConnectionId(Guid.NewGuid());
    static public readonly ConnectionId System = new ConnectionId(Guid.Empty);
}

public readonly record struct PrincipalId(string Value) {
    static public readonly PrincipalId Anonymous = new PrincipalId("anonymous");
    static public readonly PrincipalId System = new PrincipalId("system");
}

public sealed class Identity(PrincipalId principal) {
    public PrincipalId Principal { get; } = principal;
    static public readonly Identity System = new Identity(PrincipalId.System);
    static public readonly Identity Anonymous = new Identity(PrincipalId.Anonymous);
}

public sealed class Session(ConnectionId connectionId, Identity identity, uint appVersion = 0) {
    public ConnectionId ConnectionId { get; } = connectionId;
    public Identity Identity { get; } = identity;
    public uint AppVersion { get; } = appVersion;
    static public readonly Session System = new Session(ConnectionId.System, Identity.System);
}
