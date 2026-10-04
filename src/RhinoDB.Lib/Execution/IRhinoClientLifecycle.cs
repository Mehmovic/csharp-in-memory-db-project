using RhinoDB.Lib.Realtime;

namespace RhinoDB.Lib.Execution;

internal interface IRhinoClientLifecycle {
    Task<Result> OnClientConnectAsync(Session session);
    Task<Result> OnClientDisconnectAsync(Session session);
}
