using RhinoDB.Lib.Realtime;

namespace RhinoDB.Lib.Execution;

public sealed class RhinoContext<TDb>(TDb db, Session session) where TDb : notnull {
    public TDb Db { get; } = db;
    public Session Session { get; } = session;
}
