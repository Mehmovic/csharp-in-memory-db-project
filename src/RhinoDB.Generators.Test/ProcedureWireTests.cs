using System.Reflection;

using RhinoDB.SchemaContracts;

namespace RhinoDB.Generators.Test;

// A procedure's args envelope is written by hand (a generator can't hand MemoryPack's or MessagePack's generators a type),
// so these pin it against what the real serializers produce for the equivalent type: a client that models the args as a
// [MemoryPackable(VersionTolerant)] class, or a [MessagePackObject] with [Key(i)] members, talks to the server unchanged.
// Also pins version tolerance both ways. Raw is covered end to end by ProcedureTests.
[TestFixture(ClientProtocolKind.VersionedMemoryPack)]
[TestFixture(ClientProtocolKind.MessagePack)]
public class ProcedureWireTests(ClientProtocolKind protocol) {
    private const string Source = """
        using System;
        using System.Linq;
        using System.Threading.Tasks;
        using MemoryPack;
        using MessagePack;
        using RhinoDB.Core;
        using RhinoDB.Core.Procedures;
        using RhinoDB.Core.Tables;
        using RhinoDB.Lib.Execution;
        using RhinoDB.Lib.Hosting;
        using RhinoDB.Lib.Procedures;
        using RhinoDB.Lib.Realtime;

        namespace TestNs;

        [Database]
        public partial class RootDb : DbContext<RootDbTransaction> { }

        [CustomType]
        [MemoryPackable(GenerateType.VersionTolerant)]
        [MessagePackObject]
        public readonly partial record struct Money(
            [property: MemoryPackOrder(0)] [property: Key(0)] long Amount,
            [property: MemoryPackOrder(1)] [property: Key(1)] string Currency);

        public enum Color { Red, Green, Blue }

        public static class Procs {
            [Procedure]
            public static Task<Result<Money>> Echo(RhinoCtx ctx, int count, string name, Money money, int[] numbers, Color color) =>
                Task.FromResult(Result.Ok(new Money(money.Amount + count + numbers.Sum() + (int)color, $"{money.Currency}/{name}")));
        }

        // What a client in each format would declare for Echo's arguments.
        [MemoryPackable(GenerateType.VersionTolerant)]
        public partial class EchoArgsMemoryPack {
            [MemoryPackOrder(0)] public int Count { get; set; }
            [MemoryPackOrder(1)] public string Name { get; set; } = "";
            [MemoryPackOrder(2)] public Money Money { get; set; }
            [MemoryPackOrder(3)] public int[] Numbers { get; set; } = [];
            [MemoryPackOrder(4)] public Color Color { get; set; }
        }

        [MessagePackObject]
        public class EchoArgsMessagePack {
            [Key(0)] public int Count { get; set; }
            [Key(1)] public string Name { get; set; } = "";
            [Key(2)] public Money Money { get; set; }
            [Key(3)] public int[] Numbers { get; set; } = [];
            [Key(4)] public Color Color { get; set; }
        }

        public static class TestHelpers {
            static readonly Money SampleMoney = new Money(5, "EUR");
            static readonly int[] SampleNumbers = [1, 2];

            public static byte[] Generated() =>
                GeneratedProcedures.TestNs_Procs_Echo.EncodeArgs(7, "ada", SampleMoney, SampleNumbers, Color.Blue);

            public static byte[] ByTheRealMemoryPackSerializer() =>
                MemoryPackSerializer.Serialize(new EchoArgsMemoryPack { Count = 7, Name = "ada", Money = SampleMoney, Numbers = SampleNumbers, Color = Color.Blue });

            public static byte[] ByTheRealMessagePackSerializer() =>
                MessagePackSerializer.Serialize(new EchoArgsMessagePack { Count = 7, Name = "ada", Money = SampleMoney, Numbers = SampleNumbers, Color = Color.Blue });

            public static string Describe(byte[] body) {
                GeneratedProcedures.TestNs_Procs_Echo.DecodeArgs(body, out var count, out var name, out var money, out var numbers, out var color);
                return $"{count}|{name}|{money.Amount}:{money.Currency}|{(numbers is null ? "null" : string.Join(",", numbers))}|{color}";
            }

            // An older client that only knew the first two parameters, and a newer one with a sixth.
            public static byte[] OlderClient(bool memoryPack) => memoryPack
                ? ProcedureEnvelope.WriteMemoryPackObject(MemoryPackSerializer.Serialize(7), MemoryPackSerializer.Serialize("ada"))
                : MessagePackSerializer.Serialize((7, "ada"));

            public static byte[] NewerClient(bool memoryPack) => memoryPack
                ? ProcedureEnvelope.WriteMemoryPackObject(
                    MemoryPackSerializer.Serialize(7), MemoryPackSerializer.Serialize("ada"), MemoryPackSerializer.Serialize(SampleMoney),
                    MemoryPackSerializer.Serialize(SampleNumbers), MemoryPackSerializer.Serialize(Color.Blue), MemoryPackSerializer.Serialize(99L))
                : MessagePackSerializer.Serialize((7, "ada", SampleMoney, SampleNumbers, Color.Blue, 99L));

            public static string ResultRoundTrip() {
                var bytes = GeneratedProcedures.TestNs_Procs_Echo.EncodeResult(new Money(42, "USD"));
                var back = GeneratedProcedures.TestNs_Procs_Echo.DecodeResult(bytes);
                return $"{back.Amount}:{back.Currency}";
            }

            public static async Task<string> Dispatch(string dir) {
                var host = (await RhinoHostBuilder.Create(dir)
                    .OnUnrecoverableError(UnrecoverableErrorPolicy.Callback(_ => { }))
                    .AddGeneratedProcedures()
                    .AddDatabase<RootDb, RootDbTransaction>(o => o.CreateDb = cold => new RootDb(cold))
                    .BuildAsync()).Unwrap();
                try {
                    var session = new Session(ConnectionId.NewId(), Identity.Anonymous);
                    var reply = await host.DispatchProcedureAsync(GeneratedProcedures.TestNs_Procs_Echo.Hash, session, Generated(), default);
                    var money = GeneratedProcedures.TestNs_Procs_Echo.DecodeResult(reply.Unwrap());
                    return $"{money.Amount}:{money.Currency}";
                } finally {
                    var root = host.GetDatabase<RootDb>();
                    var cold = (RhinoDB.Lib.Cold.ColdStore?)typeof(DbContext<RootDbTransaction>).GetProperty("Cold", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(root);
                    host.Dispose();
                    cold?.Dispose();
                }
            }
        }
        """;

    private Assembly asm = null!;

    [OneTimeSetUp]
    public void Compile() => (asm, _) = GeneratorTestHost.CompileAndLoadWithSerializationGeneratorsAndClientProtocol(Source, protocol);

    private object? Helper(string method, params object?[] args) => GeneratorTestHost.InvokeHelper(asm, "TestNs.TestHelpers", method, args);

    private bool IsMemoryPack => protocol == ClientProtocolKind.VersionedMemoryPack;

    [Test]
    public void TheArgsEnvelope_IsByteIdentical_ToTheRealSerializersOutputForTheEquivalentType() {
        var generated = (byte[])Helper("Generated")!;
        var real = (byte[])Helper(IsMemoryPack ? "ByTheRealMemoryPackSerializer" : "ByTheRealMessagePackSerializer")!;

        Assert.That(generated, Is.EqualTo(real));
    }

    [Test]
    public void TheArgsEnvelope_RoundTripsEveryParameter() {
        Assert.That(Helper("Describe", (byte[])Helper("Generated")!), Is.EqualTo("7|ada|5:EUR|1,2|Blue"));
    }

    [Test]
    public void AnOlderClientsShorterArgs_Decode_WithTheMissingParametersDefaulted() {
        Assert.That(Helper("Describe", (byte[])Helper("OlderClient", IsMemoryPack)!), Is.EqualTo("7|ada|0:|null|Red"));
    }

    [Test]
    public void ANewerClientsExtraArgs_AreIgnored() {
        Assert.That(Helper("Describe", (byte[])Helper("NewerClient", IsMemoryPack)!), Is.EqualTo("7|ada|5:EUR|1,2|Blue"));
    }

    [Test]
    public void TheResult_RoundTrips() {
        Assert.That(Helper("ResultRoundTrip"), Is.EqualTo("42:USD"));
    }

    [Test]
    public async Task ARealDispatch_DecodesTheArgsAndEncodesTheResult_InTheProjectsProtocol() {
        var dir = Path.Combine(Path.GetTempPath(), "rhinodb-procedure-wire-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, GeneratorConfigLoader.ConfigFileName),
            $$"""{ "Host": { "ColdPath": {{System.Text.Json.JsonSerializer.Serialize(dir)}}, "HttpEnabled": false, "HttpPort": 0 } }""");
        try {
            // 5 + 7 + (1 + 2) + Blue (2)
            Assert.That(await (Task<string>)Helper("Dispatch", dir)!, Is.EqualTo("17:EUR/ada"));
        } finally {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
