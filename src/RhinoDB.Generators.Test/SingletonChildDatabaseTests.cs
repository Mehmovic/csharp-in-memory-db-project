using System.Reflection;
using System.Text.Json;

using RhinoDB.Core;
using RhinoDB.Lib.Cold;
using RhinoDB.Lib.Execution;
using RhinoDB.Lib.Hosting;
using RhinoDB.SchemaContracts;

namespace RhinoDB.Generators.Test;

// [ChildDatabase<TRoot>]: one instance, no key, activated with the host, never disposed. Real generated Root +
// singleton + keyed Child, driven through a real RhinoHost - the keyed Child is there to prove both forms coexist.
public class SingletonChildDatabaseTests {
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

        [ChildDatabase<RootDb>]
        public partial class MarketDb : DbContext<MarketDbTransaction> { }

        [ChildDatabase<RootDb, string>]
        public partial class SessionDb : DbContext<SessionDbTransaction> { }

        [Table<RootDb>(TableKind.Persistent)]
        [MemoryPackable]
        [MessagePackObject]
        public readonly partial record struct Wallet([PrimaryKey] [property: Key(0)] int Id, [property: Key(1)] int Coins);

        [Table<MarketDb>(TableKind.Persistent)]
        [MemoryPackable]
        [MessagePackObject]
        public readonly partial record struct Listing([PrimaryKey] [property: Key(0)] int Id, [property: Key(1)] int Price);

        [Table<SessionDb>(TableKind.Persistent)]
        [MemoryPackable]
        [MessagePackObject]
        public readonly partial record struct Match([PrimaryKey] [property: Key(0)] int Id);

        public static class TestHelpers {
            public static async Task<RhinoHost> BuildWithoutTouchingTheSingleton(string dir) =>
                (await RhinoHostBuilder.Create(dir)
                    .OnUnrecoverableError(UnrecoverableErrorPolicy.Callback(_ => { }))
                    .AddGeneratedChildDatabases()
                    .AddDatabase<RootDb, RootDbTransaction>(o => o.CreateDb = cold => new RootDb(cold))
                    .BuildAsync()).Unwrap();

            public static async Task<RhinoHost> Build(string dir) {
                var host = await BuildWithoutTouchingTheSingleton(dir);
                await new RootDbLoader().LoadAsync(host.GetDatabase<RootDb>());
                return host;
            }

            public static async Task<MarketDb> Market(RhinoHost host) =>
                (await host.GetOrActivateChildAsync<MarketDb, MarketDbTransaction, string>(SingletonChild.Key)).Unwrap();

            static RhinoCtx Ctx(RhinoHost host) => new RhinoCtx(host, Identity.Anonymous);

            // Keyless, like the Root: the body's `tx.Listing` is what picks MarketDb over RootDb.
            public static Task<Result> InsertListing(RhinoHost host, int id, int price) =>
                Ctx(host).BeginTx((db, tx) => { tx.Listing.Insert(new Listing(id, price)); return Result.Ok(); });

            public static async Task<bool> ListingExists(RhinoHost host, int id) =>
                (await Ctx(host).BeginTx((db, tx) => Result.Ok(tx.Listing.Primary.Find(id).HasRow()))).Unwrap();

            public static async Task<bool> WalletExists(RhinoHost host, int id) =>
                (await Ctx(host).BeginTx((db, tx) => Result.Ok(tx.Wallet.Primary.Find(id).HasRow()))).Unwrap();

            // Buying takes the listing off the market and records the purchase in the Root - one transaction.
            static PlannedMultiTx Buy(RhinoHost host, int listingId, int walletId, bool rootFails) =>
                Ctx(host).PlanMultiTx()
                    .Add(static (db, tx, id) => { tx.Listing.Delete(id); return Result.Ok(); }, listingId)
                    .Add(static (db, tx, s) => {
                        tx.Wallet.Insert(new Wallet(s.Id, 1));
                        return s.Fails ? Result.Error(DbError.Custom(1)) : Result.Ok();
                    }, (Id: walletId, Fails: rootFails));

            public static Task<Result> PlannedBuy(RhinoHost host, int listingId, int walletId, bool rootFails) =>
                Buy(host, listingId, walletId, rootFails).Commit();

            public static Task<Result> PlannedBuyThenCrash(RhinoHost host, int listingId, int walletId, MultiTxCrashPoint crashAt) {
                var multi = Buy(host, listingId, walletId, rootFails: false);
                multi.TestOnlySimulateCrashAt = crashAt;
                return multi.Commit();
            }

            public static async Task<(Result Committed, int Price)> LockedBuy(RhinoHost host, int listingId, int walletId) {
                await using var tx = (await Ctx(host).LockMultiTx(p => p.RootDb().MarketDb())).Unwrap();
                var price = (await tx.Run(static (db, t, id) => Result.Ok(t.Listing.Primary.Find(id).Get().Unwrap().Price), listingId)).Unwrap();
                (await tx.Run(static (db, t, id) => { t.Listing.Delete(id); return Result.Ok(); }, listingId)).ThrowIfError();
                (await tx.Run(static (db, t, s) => { t.Wallet.Insert(new Wallet(s.Id, s.Price)); return Result.Ok(); }, (Id: walletId, Price: price))).ThrowIfError();
                return (await tx.Commit(), price);
            }

            public static Task<Result> DisposeMarket(RhinoHost host) =>
                host.DisposeChildAsync<MarketDb, MarketDbTransaction, string>(SingletonChild.Key);

            public static async Task<Result> ActivateMarketUnderAnotherKey(RhinoHost host) =>
                (await host.GetOrActivateChildAsync<MarketDb, MarketDbTransaction, string>("other")).Void();

            // Skips the generated SessionDbLoader: activation must run the supplied Load instead, exactly once.
            public static int CustomLoads;
            public static async Task<RhinoHost> BuildWithCustomChildLoad(string dir) {
                CustomLoads = 0;
                return (await RhinoHostBuilder.Create(dir)
                    .OnUnrecoverableError(UnrecoverableErrorPolicy.Callback(_ => { }))
                    .AddDatabase<RootDb, RootDbTransaction>(o => o.CreateDb = cold => new RootDb(cold))
                    .AddChildDatabase<SessionDb, SessionDbTransaction, string>(o => {
                        o.CreateDb = cold => new SessionDb(cold);
                        o.Load = static db => { CustomLoads++; return Task.CompletedTask; };
                    })
                    .BuildAsync()).Unwrap();
            }

            public static async Task<bool> MatchExists(RhinoHost host, int id) =>
                (await Ctx(host).BeginTx("s1", (db, tx) => Result.Ok(tx.Match.Primary.Find(id).HasRow()))).Unwrap();

            public static async Task<Result> UseKeyedChild(RhinoHost host) =>
                await Ctx(host).BeginTx("s1", (db, tx) => { tx.Match.Insert(new Match(1)); return Result.Ok(); });
        }
        """;

    private string dir = "";
    private Assembly asm = null!;

    [OneTimeSetUp]
    public void Compile() => (asm, _) = GeneratorTestHost.CompileAndLoad(Source);

    [SetUp]
    public void SetUp() {
        dir = Path.Combine(Path.GetTempPath(), "rhinodb-singleton-child-tests", Guid.NewGuid().ToString("N"));
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

    private Task<RhinoHost> Build() => (Task<RhinoHost>)Helper("Build", dir)!;

    private async Task<Result> Call(string method, params object?[] args) => await (Task<Result>)Helper(method, args)!;

    private async Task<bool> Exists(string method, RhinoHost host, int id) => await (Task<bool>)Helper(method, host, id)!;

    private string SingletonDirectory => Path.Combine(dir, "Children", "MarketDb", "singleton");

    // The Root's ColdStore is internal and isn't closed by host.Dispose (a real process exit closes it) - close it by
    // hand so the same directory can be reopened in-process. Children close via host.Dispose.
    static private void Shutdown(RhinoHost host) {
        var root = host.GetDatabase<object>();
        var cold = (ColdStore)root.GetType().GetProperty("Cold", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(root)!;
        host.Dispose();
        cold.Dispose();
    }

    [Test]
    public async Task TheSingleton_IsActivatedWhenTheHostIsBuilt_BeforeAnyRequestTouchesIt() {
        var host = await (Task<RhinoHost>)Helper("BuildWithoutTouchingTheSingleton", dir)!;

        Assert.That(Directory.Exists(SingletonDirectory), Is.True, "it lives like the Root - opened and recovered at startup, not on first use.");
        Shutdown(host);
    }

    [Test]
    public async Task KeylessBeginTx_WritesAndReadsTheSingleton() {
        var host = await Build();

        Assert.That((await Call("InsertListing", host, 1, 50)).IsOk(), Is.True);

        Assert.That(await Exists("ListingExists", host, 1), Is.True);
        Assert.That(await Exists("WalletExists", host, 1), Is.False, "the keyless body bound to MarketDb, not the Root.");
        Shutdown(host);
    }

    [Test]
    public async Task TheSingletonsData_SurvivesARestart() {
        var host = await Build();
        Assert.That((await Call("InsertListing", host, 2, 50)).IsOk(), Is.True);
        Shutdown(host);

        var restarted = await Build();

        Assert.That(await Exists("ListingExists", restarted, 2), Is.True);
        Shutdown(restarted);
    }

    [Test]
    public async Task AKeyedChild_StillWorksAlongsideASingleton() {
        var host = await Build();

        Assert.That((await Call("UseKeyedChild", host)).IsOk(), Is.True);
        Assert.That(Directory.Exists(Path.Combine(dir, "Children", "SessionDb", "s1")), Is.True);
        Shutdown(host);
    }

    [Test]
    public async Task PlannedMultiTx_AcrossRootAndSingleton_CommitsBoth() {
        var host = await Build();
        Assert.That((await Call("InsertListing", host, 10, 70)).IsOk(), Is.True);

        var bought = await Call("PlannedBuy", host, 10, 10, false);

        Assert.That(bought.IsOk(), Is.True);
        Assert.That(await Exists("ListingExists", host, 10), Is.False);
        Assert.That(await Exists("WalletExists", host, 10), Is.True);
        Shutdown(host);
    }

    [Test]
    public async Task PlannedMultiTx_AcrossRootAndSingleton_AFailingRootStep_RevertsTheSingletonsShare() {
        var host = await Build();
        Assert.That((await Call("InsertListing", host, 11, 70)).IsOk(), Is.True);

        var bought = await Call("PlannedBuy", host, 11, 11, true);

        Assert.That(bought.IsError(), Is.True);
        Assert.That(await Exists("ListingExists", host, 11), Is.True, "the listing's delete was undone.");
        Assert.That(await Exists("WalletExists", host, 11), Is.False);
        Shutdown(host);
    }

    [Test]
    public async Task LockedMultiTx_DeclaresTheSingletonWithoutAKey_AndItsValueDrivesTheRootStep() {
        var host = await Build();
        Assert.That((await Call("InsertListing", host, 20, 85)).IsOk(), Is.True);

        var (committed, price) = await (Task<(Result, int)>)Helper("LockedBuy", host, 20, 20)!;

        Assert.That(committed.IsOk(), Is.True);
        Assert.That(price, Is.EqualTo(85));
        Assert.That(await Exists("ListingExists", host, 20), Is.False);
        Assert.That(await Exists("WalletExists", host, 20), Is.True);
        Shutdown(host);
    }

    [Test]
    public async Task ACrashAfterEveryPrepareIsDurable_IsCompletedAtStartup_OnTheRootAndTheSingleton() {
        // Both shares are durable, so this is a real two-phase commit - and the singleton's half is recovered by
        // its eager activation at startup, not whenever something happens to touch it.
        var host = await Build();
        Assert.That((await Call("InsertListing", host, 30, 10)).IsOk(), Is.True);
        await Call("PlannedBuyThenCrash", host, 30, 30, MultiTxCrashPoint.AfterAllDurablePrepares);
        Shutdown(host);

        var restarted = await Build();

        Assert.That(await Exists("ListingExists", restarted, 30), Is.False);
        Assert.That(await Exists("WalletExists", restarted, 30), Is.True);
        Shutdown(restarted);
    }

    [Test]
    public async Task ACrashBeforeEveryPrepareIsDurable_IsAbortedAtStartup_OnTheRootAndTheSingleton() {
        var host = await Build();
        Assert.That((await Call("InsertListing", host, 31, 10)).IsOk(), Is.True);
        await Call("PlannedBuyThenCrash", host, 31, 31, MultiTxCrashPoint.AfterFirstDurablePrepare);
        Shutdown(host);

        var restarted = await Build();

        Assert.That(await Exists("ListingExists", restarted, 31), Is.True);
        Assert.That(await Exists("WalletExists", restarted, 31), Is.False);
        Shutdown(restarted);
    }

    [Test]
    public async Task DisposingTheSingleton_IsRefused_AndItsDataStays() {
        var host = await Build();
        Assert.That((await Call("InsertListing", host, 40, 5)).IsOk(), Is.True);

        var disposed = await Call("DisposeMarket", host);

        Assert.That(disposed.GetError().Kind, Is.EqualTo(ErrorKind.ChildDatabaseIsSingleton));
        Assert.That(Directory.Exists(SingletonDirectory), Is.True);
        Assert.That(await Exists("ListingExists", host, 40), Is.True);
        Shutdown(host);
    }

    [Test]
    public async Task ActivatingTheSingletonUnderAnyOtherKey_IsRefused() {
        var host = await Build();

        var other = await Call("ActivateMarketUnderAnotherKey", host);

        Assert.That(other.GetError().Kind, Is.EqualTo(ErrorKind.ChildDatabaseIsSingleton), "a second instance must never be created.");
        Assert.That(Directory.Exists(Path.Combine(dir, "Children", "MarketDb", "other")), Is.False);
        Shutdown(host);
    }

    [Test]
    public async Task AKeyedChild_IsLoadedFromColdOnActivation_WithoutTheCallerLoadingIt() {
        var host = await Build();
        Assert.That((await Call("UseKeyedChild", host)).IsOk(), Is.True);
        Shutdown(host);

        var restarted = await (Task<RhinoHost>)Helper("BuildWithoutTouchingTheSingleton", dir)!;

        Assert.That(await Exists("MatchExists", restarted, 1), Is.True, "first touch activates it, recovers it, and loads its Persistent rows.");
        Shutdown(restarted);
    }

    [Test]
    public async Task ASuppliedLoad_ReplacesTheGeneratedLoader_AndRunsOncePerActivation() {
        var host = await Build();
        Assert.That((await Call("UseKeyedChild", host)).IsOk(), Is.True);
        Shutdown(host);

        var restarted = await (Task<RhinoHost>)Helper("BuildWithCustomChildLoad", dir)!;
        var exists = await Exists("MatchExists", restarted, 1);
        await Exists("MatchExists", restarted, 1);

        var loads = (int)asm.GetType("TestNs.TestHelpers")!.GetField("CustomLoads")!.GetValue(null)!;
        Assert.That(loads, Is.EqualTo(1));
        Assert.That(exists, Is.False, "the custom load chose not to load anything - the generated loader never ran.");
        Shutdown(restarted);
    }

    [Test]
    public async Task ChildrenWithOnlyInstantTables_CompileAndActivate_KeyedAndSingleton() {
        // No Persistent table means no generated (ColdStore) ctor used to exist - while the generated registration
        // always calls `new {Child}(cold)`.
        const string source = """
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

            [ChildDatabase<RootDb>]
            public partial class LobbyDb : DbContext<LobbyDbTransaction> { }

            [ChildDatabase<RootDb, int>]
            public partial class RoomDb : DbContext<RoomDbTransaction> { }

            [Table<LobbyDb>(TableKind.Instant)]
            [MemoryPackable]
            [MessagePackObject]
            public readonly partial record struct Waiting([PrimaryKey] [property: Key(0)] int Id);

            [Table<RoomDb>(TableKind.Instant)]
            [MemoryPackable]
            [MessagePackObject]
            public readonly partial record struct Seat([PrimaryKey] [property: Key(0)] int Id);

            public static class Helpers {
                public static async Task<bool> Run(string dir) {
                    var host = (await RhinoHostBuilder.Create(dir)
                    .OnUnrecoverableError(UnrecoverableErrorPolicy.Callback(_ => { }))
                        .AddGeneratedChildDatabases()
                        .AddDatabase<RootDb, RootDbTransaction>(o => o.CreateDb = cold => new RootDb())
                        .BuildAsync()).Unwrap();
                    var ctx = new RhinoCtx(host, Identity.Anonymous);
                    (await ctx.BeginTx((db, tx) => { tx.Instant.Waiting.Insert(new Waiting(1)); return Result.Ok(); })).ThrowIfError();
                    (await ctx.BeginTx(7, (db, tx) => { tx.Instant.Seat.Insert(new Seat(1)); return Result.Ok(); })).ThrowIfError();
                    var waiting = (await ctx.BeginTx((db, tx) => Result.Ok(tx.Instant.Waiting.Primary.Find(1).HasRow()))).Unwrap();
                    var seated = (await ctx.BeginTx(7, (db, tx) => Result.Ok(tx.Instant.Seat.Primary.Find(1).HasRow()))).Unwrap();
                    host.Dispose();
                    return waiting && seated;
                }
            }
            """;

        var (instantOnly, _) = GeneratorTestHost.CompileAndLoad(source);

        Assert.That(await (Task<bool>)GeneratorTestHost.InvokeHelper(instantOnly, "TestNs.Helpers", "Run", dir)!, Is.True);
    }

    [Test]
    public void AHookTargetingASingleton_ReportsRHINO039() {
        const string source = """
            using MemoryPack;
            using MessagePack;
            using RhinoDB.Core;
            using System.Threading.Tasks;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class RootDb : DbContext<RootDbTransaction> { }

            [ChildDatabase<RootDb>]
            public partial class MarketDb : DbContext<MarketDbTransaction> { }

            public static class Hooks {
                [OnInit<MarketDb>]
                public static Task<Result> Init(RhinoCtx ctx) => Task.FromResult(Result.Ok());
            }
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO039"), "hooks stay Root-only - a singleton lives like the Root, but it isn't one.");
        Assert.That(ex.Message, Does.Contain("is a [ChildDatabase<...>], not a Root"));
    }

    [Test]
    public void AKeylessBodyValidForBothTheRootAndASingleton_NeedsTypedLambdaParameters() {
        // The accepted nuance: keyless short forms share the Root's shape, so a body that touches no table unique
        // to one database can't be told apart - same as two keyed Children sharing a key type.
        const string source = """
            using MemoryPack;
            using MessagePack;
            using RhinoDB.Core;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;
            using System.Threading.Tasks;

            namespace TestNs;

            [Database]
            public partial class RootDb : DbContext<RootDbTransaction> { }

            [ChildDatabase<RootDb>]
            public partial class MarketDb : DbContext<MarketDbTransaction> { }

            [Table<RootDb>(TableKind.Persistent)]
            [MemoryPackable]
            [MessagePackObject]
            public readonly partial record struct Wallet([PrimaryKey] [property: Key(0)] int Id);

            [Table<MarketDb>(TableKind.Persistent)]
            [MemoryPackable]
            [MessagePackObject]
            public readonly partial record struct Listing([PrimaryKey] [property: Key(0)] int Id);

            public static class Usage {
                public static Task<Result> Run(RhinoCtx ctx) => ctx.BeginTx(static (db, tx) => Result.Ok());
            }
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("CS0121"));
        Assert.That(ex.Message, Does.Not.Contain("CS1729"), "precondition: CS0121 is the only reason it failed.");
        Assert.DoesNotThrow(() => GeneratorTestHost.CompileAndLoad(
            source.Replace("static (db, tx) => Result.Ok()", "static (MarketDb db, MarketDbTransaction tx) => Result.Ok()")));
    }
}
