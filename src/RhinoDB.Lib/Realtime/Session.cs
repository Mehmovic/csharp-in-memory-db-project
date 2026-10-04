namespace RhinoDB.Lib.Realtime;

public readonly record struct SessionId(Guid Value) {
    static public SessionId NewId() => new SessionId(Guid.NewGuid());
    static public readonly SessionId System = new SessionId(Guid.Empty);
}

public readonly record struct PrincipalId(string Value) {
    static public readonly PrincipalId Anonymous = new PrincipalId("anonymous");
    static public readonly PrincipalId System = new PrincipalId("system");
}

public sealed class Session(SessionId id, PrincipalId principal) {
    public SessionId Id { get; } = id;
    public PrincipalId Principal { get; } = principal;
    static public readonly Session System = new Session(SessionId.System, PrincipalId.System);
}
