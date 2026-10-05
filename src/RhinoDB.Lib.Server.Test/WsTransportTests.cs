using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text.Json;

using RhinoDB.Core;
using RhinoDB.Core.Rpc;
using RhinoDB.Lib.Cold;
using RhinoDB.Lib.Execution;
using RhinoDB.Lib.Hosting;
using RhinoDB.Lib.Realtime;
using RhinoDB.Lib.Server.Realtime;
using RhinoDB.SchemaContracts;

namespace RhinoDB.Lib.Server.Test;

// Real WebSocket client, real dispatch through RhinoHostBuilder.AddRpcCommand, real Kestrel - the
// actual bar for Phase 3 per the plan ("extract the interface from two real implementations,"
// here: from one real implementation plus a real client, not from mocks on either side). Frame/
// Rpc encoding is hand-rolled client-side (not reusing WsTransport/RpcFrameCodec's internal types)
// so this is a genuine black-box test of the wire format, not a round-trip through shared code.
public class WsTransportTests {
    private string dir = "";

    [SetUp]
    public void SetUp() {
        dir = Path.Combine(Path.GetTempPath(), "rhinodb-wstransport-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
    }

    [TearDown]
    public void TearDown() {
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private sealed class FakeDb(ColdStore cold) : DbContext(cold);

    private sealed class LifecycleTrackingDb(ColdStore cold) : DbContext(cold) {
        public bool ConnectCalled;
        public bool DisconnectCalled;

        protected internal override Task<Result> OnClientConnectAsync(Session session) {
            ConnectCalled = true;
            return Task.FromResult(Result.Ok());
        }

        protected internal override Task<Result> OnClientDisconnectAsync(Session session) {
            DisconnectCalled = true;
            return Task.FromResult(Result.Ok());
        }
    }

    private sealed class RejectingDb(ColdStore cold, DbError error) : DbContext(cold) {
        protected internal override Task<Result> OnClientConnectAsync(Session session) => Task.FromResult(Result.Error(error));
    }

    static private async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000) {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
    }

    static private void WriteConfig(string directory, int httpPort) {
        var json = JsonSerializer.Serialize(
            new RhinoDbConfig { Host = new HostConfig { ColdPath = directory, HttpPort = httpPort } },
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(directory, GeneratorConfigLoader.ConfigFileName), json);
    }

    static private byte[] EncodeFrame(ushort type, byte[] payload) {
        var buffer = new byte[2 + payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, type);
        payload.CopyTo(buffer, 2);
        return buffer;
    }

    static private byte[] EncodeRpcRequest(uint commandHash, uint requestId, byte[] body) {
        var buffer = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, commandHash);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), requestId);
        body.CopyTo(buffer, 8);
        return buffer;
    }

    static private Task SendHelloAsync(ClientWebSocket client, uint appVersion = 1) {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, appVersion);
        return client.SendAsync(EncodeFrame((ushort)FrameType.Hello, payload), WebSocketMessageType.Binary, true, CancellationToken.None);
    }

    static private async Task<(ushort Type, byte[] Payload)> ReceiveFrameAsync(ClientWebSocket client) {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        WebSocketReceiveResult result;
        do {
            result = await client.ReceiveAsync(buffer, CancellationToken.None);
            message.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        var raw = message.ToArray();
        var type = BinaryPrimitives.ReadUInt16LittleEndian(raw);
        return (type, raw[2..]);
    }

    private async Task<(RhinoHost Host, ClientWebSocket Client)> ConnectAsync(RhinoHostBuilder builder) {
        WriteConfig(dir, 0);
        var hostResult = await builder.AddDatabase<FakeDb, DefaultTransaction>(options => {
            options.CreateDb = cold => new FakeDb(cold);
        }).BuildAsync();

        var host = hostResult.Unwrap();
        var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{host.NetworkPort}/rt"), CancellationToken.None);
        await SendHelloAsync(client);
        return (host, client);
    }

    [Test]
    public async Task RpcFrame_ForARegisteredCommand_DispatchesAndEchoesTheBodyBack() {
        var commandHash = NameHash.Compute("Echo");
        var builder = RhinoHostBuilder.Create(dir)
            .AddRpcCommand(commandHash, (_, body, _) => Task.FromResult(Result<ReadOnlyMemory<byte>>.Ok(body)));

        var (host, client) = await ConnectAsync(builder);
        using var _ = host;
        using var __ = client;

        var requestBody = "hello"u8.ToArray();
        await client.SendAsync(
            EncodeFrame((ushort)FrameType.Rpc, EncodeRpcRequest(commandHash, requestId: 42, requestBody)),
            WebSocketMessageType.Binary, true, CancellationToken.None);

        var (type, payload) = await ReceiveFrameAsync(client);

        Assert.That(type, Is.EqualTo((ushort)FrameType.RpcResult));
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(payload), Is.EqualTo(42u), "the response must echo back the SAME request_id, not the command hash.");
        Assert.That(payload[4], Is.EqualTo((byte)ErrorKind.None), "ErrorKind.None (0) means success.");
        Assert.That(payload[5..], Is.EqualTo(requestBody));
        host.GetDatabase<FakeDb>().Cold!.Dispose();
    }

    [Test]
    public async Task RpcFrame_ForAnUnregisteredCommandHash_RespondsWithUnknownRpcCommandErrorKind() {
        var (host, client) = await ConnectAsync(RhinoHostBuilder.Create(dir));
        using var _ = host;
        using var __ = client;

        await client.SendAsync(
            EncodeFrame((ushort)FrameType.Rpc, EncodeRpcRequest(commandHash: 0xDEADBEEF, requestId: 7, [])),
            WebSocketMessageType.Binary, true, CancellationToken.None);

        var (type, payload) = await ReceiveFrameAsync(client);

        Assert.That(type, Is.EqualTo((ushort)FrameType.RpcResult));
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(payload), Is.EqualTo(7u));
        Assert.That(payload[4], Is.EqualTo((byte)ErrorKind.UnknownRpcCommand),
            "a dispatch-table miss must surface as UnknownRpcCommandException's own ErrorKind, not a generic failure.");
        host.GetDatabase<FakeDb>().Cold!.Dispose();
    }

    [Test]
    public async Task NonRpcFrame_IsEchoedBackAsAck_SinceNoFanOutDispatchExistsYet() {
        var (host, client) = await ConnectAsync(RhinoHostBuilder.Create(dir));
        using var _ = host;
        using var __ = client;

        var payload = "ping"u8.ToArray();
        await client.SendAsync(EncodeFrame((ushort)FrameType.Heartbeat, payload), WebSocketMessageType.Binary, true, CancellationToken.None);

        var (type, echoedPayload) = await ReceiveFrameAsync(client);

        Assert.That(type, Is.EqualTo((ushort)FrameType.Ack));
        Assert.That(echoedPayload, Is.EqualTo(payload));
        host.GetDatabase<FakeDb>().Cold!.Dispose();
    }

    [Test]
    public async Task RealConnectAndDisconnect_FireTheDatabasesOnClientConnectAndOnClientDisconnectHooks() {
        WriteConfig(dir, 0);
        LifecycleTrackingDb? db = null;
        var hostResult = await RhinoHostBuilder.Create(dir)
            .AddDatabase<LifecycleTrackingDb, DefaultTransaction>(options => { options.CreateDb = cold => db = new LifecycleTrackingDb(cold); })
            .BuildAsync();

        var host = hostResult.Unwrap();
        using var _ = host;
        var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{host.NetworkPort}/rt"), CancellationToken.None);
        await SendHelloAsync(client);

        // AcceptConnectionAsync awaits DispatchClientConnectAsync before its read loop starts, so a
        // round-tripped frame proves OnClientConnectAsync already ran server-side - no polling needed.
        await client.SendAsync(EncodeFrame((ushort)FrameType.Heartbeat, []), WebSocketMessageType.Binary, true, CancellationToken.None);
        await ReceiveFrameAsync(client);
        Assert.That(db!.ConnectCalled, Is.True);

        // Abort (not a graceful CloseAsync handshake) - proves the disconnect hook fires from
        // AcceptConnectionAsync's `finally`, i.e. even when the read loop ends via a thrown
        // WebSocketException rather than a clean Close frame, which is the realistic "client vanished"
        // case this hook exists for.
        client.Abort();
        await WaitUntilAsync(() => db.DisconnectCalled);

        Assert.That(db.DisconnectCalled, Is.True);
        db.Cold!.Dispose();
    }

    [Test]
    public async Task OnClientConnect_RejectingWithSystemFailure_ClosesWithInternalServerErrorAndTheKindAsDescription() {
        WriteConfig(dir, 0);
        var hostResult = await RhinoHostBuilder.Create(dir)
            .AddDatabase<RejectingDb, DefaultTransaction>(options => {
                options.CreateDb = cold => new RejectingDb(cold, DbError.SystemFailure(new Exception("boom")));
            })
            .BuildAsync();

        var host = hostResult.Unwrap();
        using var _ = host;
        var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{host.NetworkPort}/rt"), CancellationToken.None);
        await SendHelloAsync(client);

        var result = await client.ReceiveAsync(new byte[256], CancellationToken.None);

        Assert.That(result.MessageType, Is.EqualTo(WebSocketMessageType.Close));
        Assert.That(client.CloseStatus, Is.EqualTo(WebSocketCloseStatus.InternalServerError));
        Assert.That(client.CloseStatusDescription, Is.EqualTo("SystemFailure"));
        // Echo the close back (same as any well-behaved client would) - otherwise Kestrel's graceful
        // shutdown waits out its own drain timeout for this still-half-closed connection when the
        // host is disposed below, making the test take ~30s for no real reason.
        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        host.GetDatabase<RejectingDb>().Cold!.Dispose();
    }

    [Test]
    public async Task OnClientConnect_RejectingWithACustomError_ClosesWithPolicyViolationAndTheKindAndCodeAsDescription() {
        WriteConfig(dir, 0);
        var hostResult = await RhinoHostBuilder.Create(dir)
            .AddDatabase<RejectingDb, DefaultTransaction>(options => {
                options.CreateDb = cold => new RejectingDb(cold, DbError.Custom(42));
            })
            .BuildAsync();

        var host = hostResult.Unwrap();
        using var _ = host;
        var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{host.NetworkPort}/rt"), CancellationToken.None);
        await SendHelloAsync(client);

        var result = await client.ReceiveAsync(new byte[256], CancellationToken.None);

        Assert.That(result.MessageType, Is.EqualTo(WebSocketMessageType.Close));
        Assert.That(client.CloseStatus, Is.EqualTo(WebSocketCloseStatus.PolicyViolation));
        Assert.That(client.CloseStatusDescription, Is.EqualTo("Custom:42"));
        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        host.GetDatabase<RejectingDb>().Cold!.Dispose();
    }

    [Test]
    public async Task Hello_WithAnAppVersionTheValidatorRejects_ClosesWithPolicyViolationAndClientAppVersionInvalidBeforeOnClientConnectEverFires() {
        WriteConfig(dir, 0);
        var hostResult = await RhinoHostBuilder.Create(dir)
            .SetAppVersionValidator(version => version < 5)
            .AddDatabase<LifecycleTrackingDb, DefaultTransaction>(options => { options.CreateDb = cold => new LifecycleTrackingDb(cold); })
            .BuildAsync();

        var host = hostResult.Unwrap();
        using var _ = host;
        var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{host.NetworkPort}/rt"), CancellationToken.None);
        await SendHelloAsync(client, appVersion: 1);

        var result = await client.ReceiveAsync(new byte[256], CancellationToken.None);

        Assert.That(result.MessageType, Is.EqualTo(WebSocketMessageType.Close));
        Assert.That(client.CloseStatus, Is.EqualTo(WebSocketCloseStatus.PolicyViolation));
        Assert.That(client.CloseStatusDescription, Is.EqualTo("ClientAppVersionInvalid"));
        Assert.That(host.GetDatabase<LifecycleTrackingDb>().ConnectCalled, Is.False,
            "an invalid app version must reject before OnClientConnect ever runs, not after.");
        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        host.GetDatabase<LifecycleTrackingDb>().Cold!.Dispose();
    }

    [Test]
    public async Task Hello_WithAnAppVersionTheValidatorAccepts_ConnectsNormally() {
        var (host, client) = await ConnectAsyncWithVersion(RhinoHostBuilder.Create(dir).SetAppVersionValidator(version => version < 5), appVersion: 10);
        using var _ = host;
        using var __ = client;

        await client.SendAsync(EncodeFrame((ushort)FrameType.Heartbeat, []), WebSocketMessageType.Binary, true, CancellationToken.None);
        var (type, _) = await ReceiveFrameAsync(client);

        Assert.That(type, Is.EqualTo((ushort)FrameType.Ack));
        host.GetDatabase<FakeDb>().Cold!.Dispose();
    }

    [Test]
    public void NoValidatorConfigured_NeverRejectsAnyAppVersion() {
        Assert.DoesNotThrowAsync(async () => {
            var (host, client) = await ConnectAsync(RhinoHostBuilder.Create(dir));
            using var _ = host;
            using var __ = client;
            host.GetDatabase<FakeDb>().Cold!.Dispose();
        });
    }

    private async Task<(RhinoHost Host, ClientWebSocket Client)> ConnectAsyncWithVersion(RhinoHostBuilder builder, uint appVersion) {
        WriteConfig(dir, 0);
        var hostResult = await builder.AddDatabase<FakeDb, DefaultTransaction>(options => {
            options.CreateDb = cold => new FakeDb(cold);
        }).BuildAsync();

        var host = hostResult.Unwrap();
        var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{host.NetworkPort}/rt"), CancellationToken.None);
        await SendHelloAsync(client, appVersion);
        return (host, client);
    }
}
