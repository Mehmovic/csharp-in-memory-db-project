using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text.Json;

using RhinoDB.Core;
using RhinoDB.Lib.Cold;
using RhinoDB.Lib.Execution;
using RhinoDB.Lib.Hosting;
using RhinoDB.Lib.Realtime;
using RhinoDB.Lib.Server.Realtime;
using RhinoDB.Lib.Server.Test.Wire;
using RhinoDB.SchemaContracts;

namespace RhinoDB.Lib.Server.Test;

// Real WebSocket client, real generated [Procedure] handlers (Wire/WireSchema.cs), real Kestrel - the
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

    static private byte[] EncodeRpcRequest(uint procedureHash, uint requestId, byte[] body) {
        var buffer = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, procedureHash);
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

    // RpcResult: requestId u32 | kind u8 | customCode u16 | body
    private readonly record struct RpcReply(uint RequestId, ErrorKind Kind, ushort CustomCode, byte[] Body);

    static private RpcReply DecodeReply(byte[] payload) => new RpcReply(
        BinaryPrimitives.ReadUInt32LittleEndian(payload),
        (ErrorKind)payload[4],
        BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(5)),
        payload[7..]);

    private readonly List<Lib.Procedures.ProcedureFault> faults = [];
    private int unrecoverable;

    private async Task<(RhinoHost Host, ClientWebSocket Client)> ConnectToWireDbAsync() {
        WriteConfig(dir, 0);
        var hostResult = await RhinoHostBuilder.Create(dir)
            .AddGeneratedProcedures()
            .OnProcedureFault(faults.Add)
            .OnUnrecoverableError(UnrecoverableErrorPolicy.Callback(_ => Interlocked.Increment(ref unrecoverable)))
            .AddDatabase<WireDb, WireDbTransaction>(options => options.CreateDb = cold => new WireDb(cold))
            .BuildAsync();

        var host = hostResult.Unwrap();
        var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{host.NetworkPort}/rt"), CancellationToken.None);
        await SendHelloAsync(client);
        return (host, client);
    }

    static private async Task<RpcReply> CallAsync(ClientWebSocket client, uint procedureHash, uint requestId, byte[] args) {
        await client.SendAsync(EncodeFrame((ushort)FrameType.Rpc, EncodeRpcRequest(procedureHash, requestId, args)), WebSocketMessageType.Binary, true, CancellationToken.None);
        var (type, payload) = await ReceiveFrameAsync(client);
        Assert.That(type, Is.EqualTo((ushort)FrameType.RpcResult));
        return DecodeReply(payload);
    }

    [Test]
    public async Task ATransactionProcedure_RepliesWithTheHeaderOnly_AndAGeneralProcedure_WithItsEncodedValue() {
        var (host, client) = await ConnectToWireDbAsync();
        using var _ = host;
        using var __ = client;

        var first = await CallAsync(client, GeneratedProcedures.RhinoDB_Lib_Server_Test_Wire_WireProcedures_Add.Hash, 42,
            GeneratedProcedures.RhinoDB_Lib_Server_Test_Wire_WireProcedures_Add.EncodeArgs(1, 5));
        await CallAsync(client, GeneratedProcedures.RhinoDB_Lib_Server_Test_Wire_WireProcedures_Add.Hash, 43,
            GeneratedProcedures.RhinoDB_Lib_Server_Test_Wire_WireProcedures_Add.EncodeArgs(1, 3));
        var read = await CallAsync(client, GeneratedProcedures.RhinoDB_Lib_Server_Test_Wire_WireProcedures_Get.Hash, 44,
            GeneratedProcedures.RhinoDB_Lib_Server_Test_Wire_WireProcedures_Get.EncodeArgs(1));

        Assert.That(first.RequestId, Is.EqualTo(42u), "the reply echoes the request id, not the procedure hash.");
        Assert.That(first.Kind, Is.EqualTo(ErrorKind.None));
        Assert.That(first.CustomCode, Is.Zero);
        Assert.That(first.Body, Is.Empty, "a transaction-only procedure returns no value - its changes reach clients through their views.");
        Assert.That(GeneratedProcedures.RhinoDB_Lib_Server_Test_Wire_WireProcedures_Get.DecodeResult(read.Body), Is.EqualTo(8), "both adds committed.");
        host.GetDatabase<WireDb>().Cold!.Dispose();
    }

    [Test]
    public async Task TheConnectionsSession_ReachesTheProcedureContext() {
        var (host, client) = await ConnectToWireDbAsync();
        using var _ = host;
        using var __ = client;

        var reply = await CallAsync(client, GeneratedProcedures.RhinoDB_Lib_Server_Test_Wire_WireProcedures_AppVersion.Hash, 1, []);

        Assert.That(GeneratedProcedures.RhinoDB_Lib_Server_Test_Wire_WireProcedures_AppVersion.DecodeResult(reply.Body), Is.EqualTo(1u), "the Hello's app version.");
        host.GetDatabase<WireDb>().Cold!.Dispose();
    }

    [Test]
    public async Task ACustomError_ReachesTheClient_AsKindAndCode_WithNoBody() {
        var (host, client) = await ConnectToWireDbAsync();
        using var _ = host;
        using var __ = client;

        var reply = await CallAsync(client, GeneratedProcedures.RhinoDB_Lib_Server_Test_Wire_WireProcedures_Reject.Hash, 7,
            GeneratedProcedures.RhinoDB_Lib_Server_Test_Wire_WireProcedures_Reject.EncodeArgs(4242));

        Assert.That(reply, Is.EqualTo(reply with { RequestId = 7, Kind = ErrorKind.Custom, CustomCode = 4242 }));
        Assert.That(reply.Body, Is.Empty);
        host.GetDatabase<WireDb>().Cold!.Dispose();
    }

    [Test]
    public async Task AnEngineKind_ReachesTheClient_AsServerUnavailable_AndDoesNotTakeTheEngineDown() {
        var (host, client) = await ConnectToWireDbAsync();
        using var _ = host;
        using var __ = client;

        var reply = await CallAsync(client, GeneratedProcedures.RhinoDB_Lib_Server_Test_Wire_WireProcedures_EngineDown.Hash, 8, []);

        Assert.That(reply.Kind, Is.EqualTo(ErrorKind.ServerUnavailable));
        Assert.That(reply.CustomCode, Is.Zero);
        Assert.That(unrecoverable, Is.Zero, "a kind a procedure returns is its outcome - only the engine's own failures are unrecoverable.");
        host.GetDatabase<WireDb>().Cold!.Dispose();
    }

    [Test]
    public async Task AProcedureThatThrows_IsProcedureFailed_WithoutItsMessage_AndTheConnectionKeepsServing() {
        var (host, client) = await ConnectToWireDbAsync();
        using var _ = host;
        using var __ = client;

        var crashed = await CallAsync(client, GeneratedProcedures.RhinoDB_Lib_Server_Test_Wire_WireProcedures_Crash.Hash, 9, []);
        var next = await CallAsync(client, GeneratedProcedures.RhinoDB_Lib_Server_Test_Wire_WireProcedures_Add.Hash, 10,
            GeneratedProcedures.RhinoDB_Lib_Server_Test_Wire_WireProcedures_Add.EncodeArgs(2, 1));

        Assert.That(crashed.Kind, Is.EqualTo(ErrorKind.ProcedureFailed));
        Assert.That(crashed.Body, Is.Empty, "no exception text, ever - the reply is exactly the 7-byte header.");
        Assert.That(faults.Single().Exception.Message, Does.Contain("secret internals"), "the server's log keeps it.");
        Assert.That(next.Kind, Is.EqualTo(ErrorKind.None));
        host.GetDatabase<WireDb>().Cold!.Dispose();
    }

    [Test]
    public async Task AnUnknownProcedureHash_IsUnknownProcedure() {
        var (host, client) = await ConnectToWireDbAsync();
        using var _ = host;
        using var __ = client;

        var reply = await CallAsync(client, 0xDEADBEEF, 7, []);

        Assert.That(reply.RequestId, Is.EqualTo(7u));
        Assert.That(reply.Kind, Is.EqualTo(ErrorKind.UnknownProcedure));
        host.GetDatabase<WireDb>().Cold!.Dispose();
    }

    [Test]
    public async Task ArgsThatDontDecode_AreProcedureArgsInvalid() {
        var (host, client) = await ConnectToWireDbAsync();
        using var _ = host;
        using var __ = client;

        var reply = await CallAsync(client, GeneratedProcedures.RhinoDB_Lib_Server_Test_Wire_WireProcedures_Add.Hash, 11, [1, 2]);

        Assert.That(reply.Kind, Is.EqualTo(ErrorKind.ProcedureArgsInvalid));
        host.GetDatabase<WireDb>().Cold!.Dispose();
    }

    [Test]
    public async Task AnRpcFrameShorterThanItsHeader_ClosesTheConnectionAsAProtocolError() {
        var (host, client) = await ConnectToWireDbAsync();
        using var _ = host;
        using var __ = client;

        await client.SendAsync(EncodeFrame((ushort)FrameType.Rpc, [1, 2, 3]), WebSocketMessageType.Binary, true, CancellationToken.None);
        var result = await client.ReceiveAsync(new byte[256], CancellationToken.None);

        Assert.That(result.MessageType, Is.EqualTo(WebSocketMessageType.Close));
        Assert.That(client.CloseStatus, Is.EqualTo(WebSocketCloseStatus.ProtocolError));
        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        host.GetDatabase<WireDb>().Cold!.Dispose();
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
