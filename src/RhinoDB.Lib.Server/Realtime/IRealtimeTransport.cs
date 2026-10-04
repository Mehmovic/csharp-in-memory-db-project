using RhinoDB.Lib.Realtime;

namespace RhinoDB.Lib.Server.Realtime;

public delegate void SessionAccepted(Session session);

public interface IRealtimeTransport {
    event SessionAccepted? OnSessionAccepted;
    ValueTask SendAsync(ConnectionId connectionId, Frame frame, Delivery delivery, CancellationToken ct);
}
