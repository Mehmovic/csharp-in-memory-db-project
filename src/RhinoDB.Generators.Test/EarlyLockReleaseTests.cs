using System.Reflection;
using System.Text.Json;

using RhinoDB.Core;
using RhinoDB.Lib.Cold;
using RhinoDB.Lib.Durability;
using RhinoDB.Lib.Hosting;
using RhinoDB.SchemaContracts;

namespace RhinoDB.Generators.Test;

// Early lock release for two-phase commit: a multi-database transaction releases its databases once its prepares
// are *written*, not once they're durable, and decides off-loop. Correctness then rests on commit dependencies:
// anything written on a database while one of its chains is still undecided must fall with that chain if it aborts.
//
// Crash simulation: MultiTxCrashPoint.AfterLocksReleased writes every prepare, releases every lock and then never
// decides. A prepare that "never reached disk" is simulated by truncating that database's WAL at the prepare's
// frame after shutdown - exactly what a crash before the fsync leaves behind.
public class EarlyLockReleaseTests {
    private const string Source = """
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

        [ChildDatabase<RootDb, string>]
        public partial class SessionDb : DbContext<SessionDbTransaction> { }

        [Table<RootDb>(TableKind.Persistent)]
        [MemoryPackable]
        [MessagePackObject]
        public readonly partial record struct Wallet([PrimaryKey] [property: Key(0)] int Id, [property: Key(1)] int Coins);

        [Table<SessionDb>(TableKind.Persistent)]
        [MemoryPackable]
        [MessagePackObject]
        public readonly partial record struct Match([PrimaryKey] [property: Key(0)] int Id, [property: Key(1)] int Score);

        public static class TestHelpers {
            public static async Task<RhinoHost> Build(string dir) {
                var host = (await RhinoHostBuilder.Create(dir)
                    .AddGeneratedChildDatabases()
                    .OnUnrecoverableError(UnrecoverableErrorPolicy.Callback(static _ => { }))
                    .AddDatabase<RootDb, RootDbTransaction>(o => o.CreateDb = cold => new RootDb(cold))
                    .BuildAsync()).Unwrap();
                await new RootDbLoader().LoadAsync(host.GetDatabase<RootDb>());
                (await host.GetOrActivateChildAsync<SessionDb, SessionDbTransaction, string>("s1")).ThrowIfError();
                (await host.GetOrActivateChildAsync<SessionDb, SessionDbTransaction, string>("s2")).ThrowIfError();
                return host;
            }

            public static async Task<RhinoHost> BuildReplay(string dir) =>
                (await RhinoHostBuilder.Create(dir)
                    .AddGeneratedChildDatabases()
                    .OnUnrecoverableError(UnrecoverableErrorPolicy.Callback(static _ => { }))
                    .AddDatabase<RootDb, RootDbTransaction>(o => {
                        o.CreateDb = cold => new RootDb(cold);
                        o.LoadFromGenesis = (db, cold, upToLsn) => new RootDbLoader().LoadFromGenesis(db, cold, upToLsn);
                    })
                    .BuildAsync()).Unwrap();

            static RhinoCtx Ctx(RhinoHost host) => new RhinoCtx(host, Identity.Anonymous);

            static PlannedMultiTx WalletAndMatch(RhinoHost host, int walletId, string session, int matchId) =>
                Ctx(host).PlanMultiTx()
                    .Add(static (db, tx, id) => { tx.Wallet.Insert(new Wallet(id, 1)); return Result.Ok(); }, walletId)
                    .Add(session, static (db, tx, id) => { tx.Match.Insert(new Match(id, 1)); return Result.Ok(); }, matchId);

            public static Task<Result> TwoPhase(RhinoHost host, int walletId, string session, int matchId) =>
                WalletAndMatch(host, walletId, session, matchId).Commit();

            public static Task<Result> TwoPhaseThenCrashAfterRelease(RhinoHost host, int walletId, string session, int matchId) {
                var multi = WalletAndMatch(host, walletId, session, matchId);
                multi.TestOnlySimulateCrashAt = MultiTxCrashPoint.AfterLocksReleased;
                return multi.Commit();
            }

            public static Task<Result> InsertWallet(RhinoHost host, int id) =>
                Ctx(host).BeginTx((db, tx) => { tx.Wallet.Insert(new Wallet(id, 1)); return Result.Ok(); });

            public static Task<Result> InsertWalletConfirmed(RhinoHost host, int id) =>
                host.GetDatabase<RootDb>().RunConfirmed((db, tx) => { tx.Wallet.Insert(new Wallet(id, 1)); return Result.Ok(); }).AsTask();

            public static async Task<bool> WalletExists(RhinoHost host, int id) =>
                (await Ctx(host).BeginTx((db, tx) => Result.Ok(tx.Wallet.Primary.Find(id).HasRow()))).Unwrap();

            public static async Task<bool> MatchExists(RhinoHost host, string session, int id) =>
                (await Ctx(host).BeginTx(session, (db, tx) => Result.Ok(tx.Match.Primary.Find(id).HasRow()))).Unwrap();

            public static async Task<object> Child(RhinoHost host, string session) =>
                (await host.GetOrActivateChildAsync<SessionDb, SessionDbTransaction, string>(session)).Unwrap();
        }
        """;

    private string dir = "";
    private Assembly asm = null!;

    [OneTimeSetUp]
    public void Compile() => (asm, _) = GeneratorTestHost.CompileAndLoad(Source);

    [SetUp]
    public void SetUp() {
        dir = Path.Combine(Path.GetTempPath(), "rhinodb-elr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        WriteConfig("run");
    }

    [TearDown]
    public void TearDown() {
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private void WriteConfig(string mode) {
        var json = JsonSerializer.Serialize(
            new RhinoDbConfig { Host = new HostConfig { ColdPath = dir, HttpPort = 0, HttpEnabled = false, Mode = mode } },
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(dir, GeneratorConfigLoader.ConfigFileName), json);
    }

    private object? Helper(string method, params object?[] args) => GeneratorTestHost.InvokeHelper(asm, "TestNs.TestHelpers", method, args);

    private Task<RhinoHost> Build() => (Task<RhinoHost>)Helper("Build", dir)!;

    private Task<Result> Call(string method, params object?[] args) => (Task<Result>)Helper(method, args)!;

    private async Task<bool> WalletExists(RhinoHost host, int id) => await (Task<bool>)Helper("WalletExists", host, id)!;

    private async Task<bool> MatchExists(RhinoHost host, string session, int id) => await (Task<bool>)Helper("MatchExists", host, session, id)!;

    private string WalOf(string session) => Path.Combine(dir, "Children", "SessionDb", session, "wal.dat");

    static private ColdStore ColdOf(object db) =>
        (ColdStore)db.GetType().GetProperty("Cold", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(db)!;

    static private void Shutdown(RhinoHost host) {
        var cold = ColdOf(host.GetDatabase<object>());
        host.Dispose();
        cold.Dispose();
    }

    // Cuts the WAL at its first chain prepare: the prepare and everything after it never reached disk.
    static private void TruncateAtFirstChainPrepare(string walPath) {
        var bytes = File.ReadAllBytes(walPath);
        var offset = WalFileHeaderCodec.Size;
        while (offset < bytes.Length) {
            var status = WalRecordCodec.TryDecode(bytes.AsSpan(offset), out var entry, out var consumed);
            Assert.That(status, Is.EqualTo(WalScanStatus.Clean), "precondition: the WAL decodes cleanly up to the prepare.");
            if (entry.Kind == WalEntryKind.ChainPrepare) {
                using var stream = new FileStream(walPath, FileMode.Open, FileAccess.Write);
                stream.SetLength(offset);
                return;
            }
            offset += consumed;
        }
        Assert.Fail($"precondition: no chain prepare found in {walPath}.");
    }

    [Test]
    public async Task ARootRequest_RunsWhileTheChildPrepareIsStillFlushing() {
        var host = await Build();
        var childWal = ColdOf(await (Task<object>)Helper("Child", host, "s1")!).Wal;
        using var gate = new ManualResetEventSlim(false);
        childWal.TestOnlyBeforeFlush = () => gate.Wait(TimeSpan.FromSeconds(30));
        try {
            var commit = Call("TwoPhase", host, 700, "s1", 700);
            await Task.Delay(200);

            var other = Call("InsertWallet", host, 701);
            var finished = await Task.WhenAny(other, Task.Delay(TimeSpan.FromSeconds(5)));

            Assert.That(finished, Is.SameAs(other), "the Root was released once the prepares were written - it isn't held through the Child's fsync.");
            Assert.That((await other).IsOk(), Is.True);
            Assert.That(commit.IsCompleted, Is.False, "the chain's own reply still waits for its prepares to be durable.");

            gate.Set();
            Assert.That((await commit).IsOk(), Is.True);
        } finally {
            gate.Set();
            childWal.TestOnlyBeforeFlush = null;
        }
        Shutdown(host);
    }

    [Test]
    public async Task ARootWrite_IsNotBlockedWhileTheRootWalIsFsyncing() {
        // Early lock release only pays off if the WAL doesn't hold its append lock through the fsync - otherwise the
        // next prepare (or any write) on that database waits for the disk all the same.
        var host = await Build();
        var rootWal = ColdOf(host.GetDatabase<object>()).Wal;
        var duringFsync = typeof(WriteAheadLog).GetProperty("TestOnlyDuringFsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(duringFsync, Is.Not.Null, "WriteAheadLog.TestOnlyDuringFsync doesn't exist yet.");
        using var gate = new ManualResetEventSlim(false);
        using var entered = new ManualResetEventSlim(false);
        duringFsync!.SetValue(rootWal, (Action)(() => { entered.Set(); gate.Wait(TimeSpan.FromSeconds(10)); }));
        try {
            var commit = Call("TwoPhase", host, 800, "s1", 800);
            Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True, "precondition: a Root fsync is in progress.");

            var other = Call("InsertWallet", host, 801);
            var finished = await Task.WhenAny(other, Task.Delay(TimeSpan.FromSeconds(5)));

            Assert.That(finished, Is.SameAs(other), "the Root's loop appends while its WAL is fsyncing - it doesn't wait for the disk.");
            gate.Set();
            Assert.That((await commit).IsOk(), Is.True);
        } finally {
            gate.Set();
            duringFsync.SetValue(rootWal, null);
        }
        Shutdown(host);
    }

    [Test]
    public async Task AConfirmedWriteOnTheRoot_WaitsForTheChainItDependsOn() {
        var host = await Build();
        var childWal = ColdOf(await (Task<object>)Helper("Child", host, "s1")!).Wal;
        using var gate = new ManualResetEventSlim(false);
        childWal.TestOnlyBeforeFlush = () => gate.Wait(TimeSpan.FromSeconds(30));
        try {
            var commit = Call("TwoPhase", host, 710, "s1", 710);
            await Task.Delay(200);

            var confirmed = Call("InsertWalletConfirmed", host, 711);
            await Task.Delay(300);
            Assert.That(confirmed.IsCompleted, Is.False,
                "Confirmed means durable - and a write that depends on an undecided chain isn't durable until that chain commits.");

            gate.Set();
            Assert.That((await commit).IsOk(), Is.True);
            Assert.That((await confirmed).IsOk(), Is.True);
        } finally {
            gate.Set();
            childWal.TestOnlyBeforeFlush = null;
        }
        Shutdown(host);
    }

    [Test]
    public async Task CrashAfterRelease_ChildPrepareLost_AbortsTheChain_AndDropsTheRootWriteThatDependedOnIt() {
        var host = await Build();
        Assert.That((await Call("InsertWallet", host, 719)).IsOk(), Is.True);
        await Call("TwoPhaseThenCrashAfterRelease", host, 720, "s1", 720);
        Assert.That((await Call("InsertWallet", host, 721)).IsOk(), Is.True, "released - the Root serves the next request at once.");
        Shutdown(host);
        TruncateAtFirstChainPrepare(WalOf("s1"));

        var restarted = await Build();

        Assert.That(await WalletExists(restarted, 719), Is.True, "written before the chain - no dependency.");
        Assert.That(await WalletExists(restarted, 720), Is.False, "the Child prepare never reached disk: the chain aborted.");
        Assert.That(await MatchExists(restarted, "s1", 720), Is.False);
        Assert.That(await WalletExists(restarted, 721), Is.False,
            "written while the chain was undecided, so it depended on it - it must not survive the chain's abort.");
        Shutdown(restarted);
    }

    [Test]
    public async Task CrashAfterRelease_BothPreparesOnDisk_CommitsTheChain_AndKeepsItsDependent() {
        var host = await Build();
        await Call("TwoPhaseThenCrashAfterRelease", host, 730, "s1", 730);
        Assert.That((await Call("InsertWallet", host, 731)).IsOk(), Is.True);
        Shutdown(host);

        var restarted = await Build();

        Assert.That(await WalletExists(restarted, 730), Is.True, "every prepare is on disk: recovery completes the chain.");
        Assert.That(await MatchExists(restarted, "s1", 730), Is.True);
        Assert.That(await WalletExists(restarted, 731), Is.True, "its dependency committed, so it stays.");
        Shutdown(restarted);
    }

    [Test]
    public async Task CrashAfterRelease_ADependentTwoPhaseCommit_IsAbortedWithItsDependency_EvenThoughItsOwnPreparesAreDurable() {
        var host = await Build();
        await Call("TwoPhaseThenCrashAfterRelease", host, 740, "s1", 740);

        // Root + s2: its Root prepare is written while chain 740 is undecided, so it depends on it.
        var dependent = Call("TwoPhase", host, 741, "s2", 741);
        await Task.Delay(500);
        Assert.That(dependent.IsCompleted, Is.False, "a 2PC never replies before the chains it depends on are decided.");
        Shutdown(host);
        TruncateAtFirstChainPrepare(WalOf("s1"));

        var restarted = await Build();

        Assert.That(await WalletExists(restarted, 740), Is.False);
        Assert.That(await MatchExists(restarted, "s1", 740), Is.False);
        Assert.That(await WalletExists(restarted, 741), Is.False, "both of its prepares are on disk, but its dependency aborted.");
        Assert.That(await MatchExists(restarted, "s2", 741), Is.False, "so its s2 share goes too - the decision is the same on every participant.");
        Shutdown(restarted);
    }

    [Test]
    public async Task AChildFlushFailingAfterRelease_AbortsTheChain_PoisonsTheParticipants_AndRecordsTheAbort() {
        var host = await Build();
        var childWal = ColdOf(await (Task<object>)Helper("Child", host, "s1")!).Wal;
        using var gate = new ManualResetEventSlim(false);
        childWal.TestOnlyBeforeFlush = () => {
            gate.Wait(TimeSpan.FromSeconds(30));
            throw new IOException("injected fsync failure");
        };
        Result commit;
        try {
            var pending = Call("TwoPhase", host, 750, "s1", 750);
            await Task.Delay(200);
            Assert.That((await Call("InsertWallet", host, 751)).IsOk(), Is.True, "Optimistic, and the Root was already released.");
            gate.Set();
            commit = await pending;
        } finally {
            gate.Set();
            childWal.TestOnlyBeforeFlush = null;
        }

        Assert.That(commit.GetError().Kind, Is.EqualTo(ErrorKind.WalDurabilityFailed));
        Assert.That((await Call("InsertWallet", host, 752)).IsError(), Is.True,
            "memory is ahead of disk and can't be reverted under the dependent's work - the Root must stop.");
        Shutdown(host);
        Assert.That(ChainLog.Open(dir).Unwrap().RetainedChainCount, Is.GreaterThanOrEqualTo(1), "the abort is recorded centrally.");

        var restarted = await Build();

        // The failed prepare's bytes still reached the file at shutdown - only the recorded abort stops recovery from
        // seeing two prepares and completing the chain.
        Assert.That(await WalletExists(restarted, 750), Is.False);
        Assert.That(await MatchExists(restarted, "s1", 750), Is.False);
        Assert.That(await WalletExists(restarted, 751), Is.False, "it depended on the aborted chain.");
        Shutdown(restarted);
    }

    [Test]
    public async Task ReplayMode_DropsAWriteWhoseDependencyAborted() {
        var host = await Build();
        await Call("TwoPhaseThenCrashAfterRelease", host, 760, "s1", 760);
        Assert.That((await Call("InsertWallet", host, 761)).IsOk(), Is.True);
        Shutdown(host);
        TruncateAtFirstChainPrepare(WalOf("s1"));
        WriteConfig("replay");

        var replayed = await (Task<RhinoHost>)Helper("BuildReplay", dir)!;

        Assert.That(await WalletExists(replayed, 760), Is.False);
        Assert.That(await WalletExists(replayed, 761), Is.False, "genesis replay applies the same dependency rule as recovery.");
        Shutdown(replayed);
    }

    [Test]
    public async Task ADatabaseWithNoTwoPhaseCommits_WritesNoDependencies() {
        var host = await Build();
        for (var id = 770; id < 775; id++) Assert.That((await Call("InsertWallet", host, id)).IsOk(), Is.True);
        Shutdown(host);

        var rootWal = WriteAheadLog.ReadEntriesShared(Path.Combine(dir, "wal.dat")).Unwrap();

        Assert.That(rootWal, Is.Not.Empty);
        Assert.That(rootWal.All(e => e.Kind == WalEntryKind.Operation), Is.True);
        // Reflected so this file compiles before DecodedWalEntry.DependsOn exists.
        var dependsOn = typeof(DecodedWalEntry).GetProperty("DependsOn");
        Assert.That(dependsOn, Is.Not.Null, "DecodedWalEntry.DependsOn doesn't exist yet.");
        Assert.That(rootWal.All(e => ((Guid[])dependsOn!.GetValue(e)!).Length == 0), Is.True,
            "dependencies only exist while a chain is in flight.");
    }
}
