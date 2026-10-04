using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.WebSockets;

using RhinoDB.Core;
using RhinoDB.Lib.Hosting;
using RhinoDB.Lib.Realtime;

namespace RhinoDB.Lib.Server.Realtime;

public sealed class WsTransport(RhinoHost host) : IRealtimeTransport {
    private readonly ConcurrentDictionary<ConnectionId, WebSocket> connections = new ConcurrentDictionary<ConnectionId, WebSocket>();

    public event SessionAccepted? OnSessionAccepted;

    public async Task AcceptConnectionAsync(WebSocket socket, CancellationToken ct) {
        // The client's first frame, always - no OnClientConnect, no Session, until this passes.
        var helloFrame = await ReceiveOneFrameAsync(socket, ct);
        if (helloFrame is not { } hello) return;
        if (hello.Type != FrameType.Hello || hello.Payload.Length != 4) {
            await socket.CloseAsync(WebSocketCloseStatus.ProtocolError, "Expected a Hello frame with a 4-byte packed app version first.", ct);
            return;
        }

        var appVersion = BinaryPrimitives.ReadUInt32LittleEndian(hello.Payload.Span);
        if (host.IsAppVersionInvalid(appVersion)) {
            var (invalidStatus, invalidDescription) = CloseStatusFor(DbError.ClientAppVersionInvalid());
            await socket.CloseAsync(invalidStatus, invalidDescription, ct);
            return;
        }

        // No real auth exists yet - every connection is anonymous (Part G, deliberately out of scope).
        var session = new Session(ConnectionId.NewId(), Identity.Anonymous, appVersion);

        var connectResult = await host.DispatchClientConnectAsync(session);
        if (connectResult.IsError()) {
            var (status, description) = CloseStatusFor(connectResult.GetError());
            await socket.CloseAsync(status, description, ct);
            return;
        }

        connections[session.ConnectionId] = socket;
        OnSessionAccepted?.Invoke(session);

        try {
            await ReadLoopAsync(session, socket, ct);
        } finally {
            connections.TryRemove(session.ConnectionId, out _);
            await host.DispatchClientDisconnectAsync(session);
        }
    }

    private async Task ReadLoopAsync(Session session, WebSocket socket, CancellationToken ct) {
        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested) {
            var frame = await ReceiveOneFrameAsync(socket, ct);
            if (frame is not { } f) return;
            await HandleFrameAsync(session, f, ct);
        }
    }

    static private async Task<Frame?> ReceiveOneFrameAsync(WebSocket socket, CancellationToken ct) {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        WebSocketReceiveResult result;
        do {
            result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) {
                if (socket.State == WebSocketState.Open)
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, ct);
                return null;
            }
            message.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        return DecodeFrame(message.ToArray());
    }

    static private (WebSocketCloseStatus Status, string Description) CloseStatusFor(DbError error) {
        var status = error.Kind == ErrorKind.SystemFailure ? WebSocketCloseStatus.InternalServerError : WebSocketCloseStatus.PolicyViolation;
        var description = error.Kind == ErrorKind.Custom ? $"{error.Kind}:{error.CustomCode}" : error.Kind.ToString();
        return (status, description);
    }

    private async Task HandleFrameAsync(Session session, Frame frame, CancellationToken ct) {
        if (frame.Type == FrameType.Rpc) {
            var request = RpcFrameCodec.DecodeRequest(frame.Payload);
            var dispatchResult = await host.DispatchRpcAsync(request.CommandHash, request.Body, ct);
            var (kind, body) = dispatchResult.IsOk()
                ? (ErrorKind.None, dispatchResult.Unwrap())
                : (dispatchResult.GetError().Kind, ReadOnlyMemory<byte>.Empty);
            var resultPayload = RpcFrameCodec.EncodeResult(request.RequestId, kind, body);
            await SendAsync(session.ConnectionId, new Frame(FrameType.RpcResult, resultPayload), Delivery.ReliableOrdered, ct);
            return;
        }

        await SendAsync(session.ConnectionId, new Frame(FrameType.Ack, frame.Payload), Delivery.ReliableOrdered, ct);
    }

    public async ValueTask SendAsync(ConnectionId connectionId, Frame frame, Delivery delivery, CancellationToken ct) {
        if (!connections.TryGetValue(connectionId, out var socket) || socket.State != WebSocketState.Open) return;
        await socket.SendAsync(EncodeFrame(frame), WebSocketMessageType.Binary, endOfMessage: true, ct);
    }

    static private byte[] EncodeFrame(Frame frame) {
        var buffer = new byte[2 + frame.Payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, (ushort)frame.Type);
        frame.Payload.Span.CopyTo(buffer.AsSpan(2));
        return buffer;
    }

    static private Frame DecodeFrame(byte[] raw) {
        var type = (FrameType)BinaryPrimitives.ReadUInt16LittleEndian(raw);
        return new Frame(type, raw.AsMemory(2));
    }
}
