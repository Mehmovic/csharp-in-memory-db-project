using System.Reflection;
using System.Text.Json;

using RhinoDB.Core;
using RhinoDB.Lib.Cold;
using RhinoDB.Lib.Hosting;
using RhinoDB.SchemaContracts;

namespace RhinoDB.Generators.Test;

// The engine-bug half of fail fast: an apply that throws and whose revert then fails leaves memory in an unknown state.
// That poisons the database with ApplyFailed, which must reach the host's unrecoverable-error policy with exit code 70
// (EX_SOFTWARE) - distinct from 74 for a failing disk. Needs a real generated table to fail inside apply/revert.
public class UnrecoverableErrorHostTests {
    private const string Source = """
        using System;
        using MemoryPack;
        using MessagePack;
        using RhinoDB.Core;
        using RhinoDB.Core.Tables;
        using RhinoDB.Lib.Execution;
        using RhinoDB.Lib.Hosting;
        using RhinoDB.Lib.Realtime;
        using System.Threading.Tasks;

        namespace TestNs;

        [Database]
        public partial class RootDb : DbContext<RootDbTransaction> { }

        [Table<RootDb>(TableKind.Persistent)]
        [MemoryPackable]
        [MessagePackObject]
        public readonly partial record struct Wallet([PrimaryKey] [property: Key(0)] int Id, [property: Key(1)] int Coins);

        public static class TestHelpers {
            public static async Task<RhinoHost> Build(string dir, Action<UnrecoverableError> report) {
                var host = (await RhinoHostBuilder.Create(dir)
                    .OnUnrecoverableError(UnrecoverableErrorPolicy.Callback(report))
                    .AddDatabase<RootDb, RootDbTransaction>(o => o.CreateDb = cold => new RootDb(cold))
                    .BuildAsync()).Unwrap();
                await new RootDbLoader().LoadAsync(host.GetDatabase<RootDb>());
                return host;
            }

            public static Task<Result> InsertWallet(RhinoHost host, int id) =>
                new RhinoCtx(host, Identity.Anonymous).BeginTx((db, tx) => { tx.Wallet.Insert(new Wallet(id, 1)); return Result.Ok(); });
        }
        """;

    private string dir = "";
    private Assembly asm = null!;

    [OneTimeSetUp]
    public void Compile() => (asm, _) = GeneratorTestHost.CompileAndLoad(Source);

    [SetUp]
    public void SetUp() {
        dir = Path.Combine(Path.GetTempPath(), "rhinodb-unrecoverable-host-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(
            new RhinoDbConfig { Host = new HostConfig { ColdPath = dir, HttpPort = 0, HttpEnabled = false, Mode = "run" } },
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(dir, GeneratorConfigLoader.ConfigFileName), json);
    }

    [TearDown]
    public void TearDown() {
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private object? Helper(string method, params object?[] args) => GeneratorTestHost.InvokeHelper(asm, "TestNs.TestHelpers", method, args);

    static private ColdStore ColdOf(object db) =>
        (ColdStore)db.GetType().GetProperty("Cold", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(db)!;

    [Test]
    public async Task AnApplyWhoseRevertAlsoFails_PoisonsTheRoot_AndReportsTheEngineBugExitCode() {
        var reported = new TaskCompletionSource<UnrecoverableError>(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = await (Task<RhinoHost>)Helper("Build", dir, (Action<UnrecoverableError>)(info => reported.TrySetResult(info)))!;
        var root = host.GetDatabase<object>();
        var walletOps = root.GetType().GetField("walletOps", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(root)!;
        var applyFault = walletOps.GetType().GetProperty("TestOnlyApplyFault")!;
        applyFault.SetValue(walletOps, (Action<int>)(_ => {
            applyFault.SetValue(walletOps, null);
            throw new InvalidOperationException("injected apply fault");
        }));
        walletOps.GetType().GetProperty("TestOnlyRevertFault")!.SetValue(walletOps, (Action)(() => throw new InvalidOperationException("injected revert fault")));

        var insert = await (Task<Result>)Helper("InsertWallet", host, 1)!;
        var info = await reported.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(insert.GetError().Kind, Is.EqualTo(ErrorKind.ApplyFailed));
        Assert.That(info.Error.Kind, Is.EqualTo(ErrorKind.ApplyFailed));
        Assert.That(info.ExitCode, Is.EqualTo(UnrecoverableExitCodes.Software), "70: memory is in an unknown state because of an engine bug, not a disk.");
        Assert.That(info.Database, Is.EqualTo("RootDb (Root)"));

        var cold = ColdOf(root);
        host.Dispose();
        cold.Dispose();
    }
}
