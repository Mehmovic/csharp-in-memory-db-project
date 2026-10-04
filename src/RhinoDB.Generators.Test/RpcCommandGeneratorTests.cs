using System.Reflection;
using System.Text.Json;

using RhinoDB.Core;
using RhinoDB.Core.Rpc;
using RhinoDB.Lib.Cold;
using RhinoDB.Lib.Execution;
using RhinoDB.Lib.Hosting;
using RhinoDB.SchemaContracts;

namespace RhinoDB.Generators.Test;

// [RpcCommand] is discovered project-wide, exactly like [OnInit]/[OnStart] - this is Part G's
// "start small" compile-time RPC codegen slice: a generated const uint hash (matching the runtime
// RpcCommandHash.Compute formula exactly) and a generated AddGeneratedRpcCommands registration
// extension, nothing more (no typed request/response binding, no client-binding codegen - those need
// their own infrastructure, deliberately deferred). TestDb/DefaultTransaction are real, statically
// available types from RhinoDB.Lib - only the [RpcCommand] method and the generated registration
// extension need to come from the dynamically-compiled fixture assembly.
public class RpcCommandGeneratorTests {
    private string dir = "";

    [SetUp]
    public void SetUp() {
        dir = Path.Combine(Path.GetTempPath(), "rhinodb-rpccommandgenerator-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(
            new RhinoDbConfig { Host = new HostConfig { ColdPath = dir, HttpPort = 0 } },
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(dir, GeneratorConfigLoader.ConfigFileName), json);
    }

    [TearDown]
    public void TearDown() {
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private sealed class TestDb(ColdStore cold) : DbContext(cold);

    [Test]
    public async Task ValidRpcCommand_HashMatchesTheRuntimeFormula_AndDispatchesToTheRealMethod() {
        const string source = """
            using System;
            using RhinoDB.Core;
            using RhinoDB.Core.Rpc;
            using RhinoDB.Lib.Hosting;
            using System.Threading;
            using System.Threading.Tasks;

            namespace TestNs;

            public static class Commands {
                [RpcCommand]
                public static Task<Result<ReadOnlyMemory<byte>>> Echo(RhinoHost host, ReadOnlyMemory<byte> body, CancellationToken ct) =>
                    Task.FromResult(Result<ReadOnlyMemory<byte>>.Ok(body));
            }
            """;

        var (asm, _) = GeneratorTestHost.CompileAndLoad(source);
        var generated = asm.GetType("RhinoDB.Lib.Hosting.GeneratedRpcCommands")!;
        var hash = (uint)generated.GetField("TestNs_Commands_EchoCommandHash")!.GetValue(null)!;

        Assert.That(hash, Is.EqualTo(RpcCommandHash.Compute("Echo")), "the generator's compile-time hash must match the runtime formula exactly.");

        var builder = RhinoHostBuilder.Create(dir);
        generated.GetMethod("AddGeneratedRpcCommands")!.Invoke(null, [builder]);
        var hostResult = await builder.AddDatabase<TestDb, DefaultTransaction>(options => options.CreateDb = cold => new TestDb(cold)).BuildAsync();
        var host = hostResult.Unwrap();

        var body = new byte[] { 1, 2, 3 };
        var dispatchResult = await host.DispatchRpcAsync(hash, body, CancellationToken.None);

        Assert.That(dispatchResult.Unwrap().ToArray(), Is.EqualTo(body));
        host.GetDatabase<TestDb>().Cold!.Dispose();
        host.Dispose();
    }

    [Test]
    public void ExplicitName_ChangesTheHashAwayFromTheMethodName() {
        const string source = """
            using System;
            using RhinoDB.Core;
            using RhinoDB.Core.Rpc;
            using RhinoDB.Lib.Hosting;
            using System.Threading;
            using System.Threading.Tasks;

            namespace TestNs;

            public static class Commands {
                [RpcCommand(Name = "Say.Hello")]
                public static Task<Result<ReadOnlyMemory<byte>>> Greet(RhinoHost host, ReadOnlyMemory<byte> body, CancellationToken ct) =>
                    Task.FromResult(Result<ReadOnlyMemory<byte>>.Ok(body));
            }
            """;

        var (asm, _) = GeneratorTestHost.CompileAndLoad(source);
        var generated = asm.GetType("RhinoDB.Lib.Hosting.GeneratedRpcCommands")!;
        var hash = (uint)generated.GetField("TestNs_Commands_GreetCommandHash")!.GetValue(null)!;

        Assert.That(hash, Is.EqualTo(RpcCommandHash.Compute("Say.Hello")));
        Assert.That(hash, Is.Not.EqualTo(RpcCommandHash.Compute("Greet")));
    }

    [Test]
    public void NoRpcCommandsAnywhere_EmitsNoGeneratedRegistrationType() {
        const string source = """
            namespace TestNs;
            public static class NothingHere { }
            """;

        var (asm, _) = GeneratorTestHost.CompileAndLoad(source);

        Assert.That(asm.GetType("RhinoDB.Lib.Hosting.GeneratedRpcCommands"), Is.Null);
    }

    [Test]
    public void InvalidSignature_NonStaticMethod_ReportsRHINO035() {
        const string source = """
            using System;
            using RhinoDB.Core;
            using RhinoDB.Core.Rpc;
            using RhinoDB.Lib.Hosting;
            using System.Threading;
            using System.Threading.Tasks;

            namespace TestNs;

            public class NotStatic {
                [RpcCommand]
                public Task<Result<ReadOnlyMemory<byte>>> Echo(RhinoHost host, ReadOnlyMemory<byte> body, CancellationToken ct) =>
                    Task.FromResult(Result<ReadOnlyMemory<byte>>.Ok(body));
            }
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO035"));
    }

    [Test]
    public void InvalidSignature_WrongReturnType_ReportsRHINO035() {
        const string source = """
            using System;
            using RhinoDB.Core.Rpc;
            using RhinoDB.Lib.Hosting;
            using System.Threading;

            namespace TestNs;

            public static class Commands {
                [RpcCommand]
                public static void Echo(RhinoHost host, ReadOnlyMemory<byte> body, CancellationToken ct) { }
            }
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO035"));
    }

    [Test]
    public void TwoCommandsSharingTheSameExplicitName_ReportsRHINO036() {
        const string source = """
            using System;
            using RhinoDB.Core;
            using RhinoDB.Core.Rpc;
            using RhinoDB.Lib.Hosting;
            using System.Threading;
            using System.Threading.Tasks;

            namespace TestNs;

            public static class CommandsA {
                [RpcCommand(Name = "Shared")]
                public static Task<Result<ReadOnlyMemory<byte>>> First(RhinoHost host, ReadOnlyMemory<byte> body, CancellationToken ct) =>
                    Task.FromResult(Result<ReadOnlyMemory<byte>>.Ok(body));
            }

            public static class CommandsB {
                [RpcCommand(Name = "Shared")]
                public static Task<Result<ReadOnlyMemory<byte>>> Second(RhinoHost host, ReadOnlyMemory<byte> body, CancellationToken ct) =>
                    Task.FromResult(Result<ReadOnlyMemory<byte>>.Ok(body));
            }
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO036"));
    }
}
