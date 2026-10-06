using System.Reflection;
using System.Text.Json;

using RhinoDB.Core;
using RhinoDB.Lib.Cold;
using RhinoDB.Lib.Hosting;
using RhinoDB.Lib.Procedures;
using RhinoDB.SchemaContracts;

namespace RhinoDB.Generators.Test;

// [Procedure] end to end below the transport: real generated handlers, dispatched through a real RhinoHost the way
// WsTransport does (host.DispatchProcedureAsync with the client's encoded args). Raw protocol; ProcedureWireTests
// covers the other two.
public class ProcedureTests {
    private const string Source = """
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using System.Threading;
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

        [ChildDatabase<RootDb>]
        public partial class MarketDb : DbContext<MarketDbTransaction> { }

        [ChildDatabase<RootDb, string>]
        public partial class SessionDb : DbContext<SessionDbTransaction> { }

        [Table<RootDb>(TableKind.Instant)]
        [MemoryPackable]
        [MessagePackObject]
        public readonly partial record struct Wallet([PrimaryKey] [property: Key(0)] int Id, [property: Key(1)] int Coins);

        [Table<MarketDb>(TableKind.Instant)]
        [MemoryPackable]
        [MessagePackObject]
        public readonly partial record struct Listing([PrimaryKey] [property: Key(0)] int Id, [property: Key(1)] int Price);

        [Table<SessionDb>(TableKind.Instant)]
        [MemoryPackable]
        [MessagePackObject]
        public readonly partial record struct Match([PrimaryKey] [property: Key(0)] int Id);

        // Transaction-only procedures return no value - what they saw in their context is written here and read back.
        [Table<RootDb>(TableKind.Instant)]
        [MemoryPackable]
        [MessagePackObject]
        public readonly partial record struct Probe([PrimaryKey] [property: Key(0)] int Id, [property: Key(1)] string Text, [property: Key(2)] long Number);

        [CustomType]
        [MemoryPackable]
        [MessagePackObject]
        public readonly partial record struct Money([property: Key(0)] long Amount, [property: Key(1)] string Currency);

        public enum Color { Red, Green, Blue }

        public static class Knobs {
            public static DbError NextError = DbError.Custom(1);
        }

        public static class Procs {
            // ---- transaction-only shape ----

            [Procedure]
            public static Result Deposit(RootDbTxCtx ctx, int walletId, int coins) {
                ctx.Tx.Instant.Wallet.Insert(new Wallet(walletId, coins));
                return Result.Ok();
            }


            [Procedure]
            public static Result DepositThenFail(RootDbTxCtx ctx, int walletId) {
                ctx.Tx.Instant.Wallet.Insert(new Wallet(walletId, 5));
                return Result.Error(DbError.Custom(7));
            }

            [Procedure]
            public static Result DepositThenThrow(RootDbTxCtx ctx, int walletId) {
                ctx.Tx.Instant.Wallet.Insert(new Wallet(walletId, 5));
                throw new InvalidOperationException("procedure bug");
            }

            [Procedure]
            public static Result WhoAmI(RootDbTxCtx ctx, int probeId) {
                var text = $"{ctx.Identity.Principal.Value}|{ctx.Session.AppVersion}|{ctx.ServerVersion}|{ctx.Timestamp.Kind}";
                ctx.Tx.Instant.Probe.Insert(new Probe(probeId, text, ctx.Timestamp.Ticks));
                return Result.Ok();
            }

            [Procedure]
            public static Result RollInTx(RootDbTxCtx ctx, int probeId) {
                var roll = ctx.Random.Next(1_000_000);
                if (roll.IsError()) return roll.Void();
                ctx.Tx.Instant.Probe.Insert(new Probe(probeId, "roll", roll.Unwrap()));
                return Result.Ok();
            }

            [Procedure(Name = "market.list")]
            public static Result List(MarketDbTxCtx ctx, int listingId, int price) {
                ctx.Tx.Instant.Listing.Insert(new Listing(listingId, price));
                return Result.Ok();
            }

            [Procedure]
            public static Task<Result<Money>> Echo(RhinoCtx ctx, Money money, int[] numbers, string[] tags, Color color, Guid id, string text) =>
                Task.FromResult(Result.Ok(new Money(money.Amount + numbers.Sum() + tags.Length + (int)color, $"{money.Currency}:{id}:{text}")));

            [Procedure]
            public static Result FailInTx(RootDbTxCtx ctx) => Result.Error(Knobs.NextError);

            // ---- general shape ----

            [Procedure]
            public static Task<Result<int>> Balance(RhinoCtx ctx, int walletId) =>
                ctx.BeginTx(static (db, tx, id) => {
                    var wallet = tx.Instant.Wallet.Primary.Find(id).Get();
                    return wallet.IsError() ? Result<int>.Error(wallet.GetError()) : wallet.Unwrap().Coins;
                }, walletId);

            [Procedure]
            public static async Task<Result<int>> AddBonus(RhinoCtx ctx, int walletId, int bonus) {
                var coins = await ctx.BeginTx(static (db, tx, id) => {
                    var wallet = tx.Instant.Wallet.Primary.Find(id).Get();
                    return wallet.IsError() ? Result<int>.Error(wallet.GetError()) : wallet.Unwrap().Coins;
                }, walletId);
                if (coins.IsError()) return coins;
                var total = coins.Unwrap() + bonus;
                var written = await ctx.BeginTx(static (db, tx, s) => {
                    tx.Instant.Wallet.Delete(s.Id);
                    tx.Instant.Wallet.Insert(new Wallet(s.Id, s.Total));
                    return Result.Ok();
                }, (Id: walletId, Total: total));
                return written.IsError() ? Result<int>.Error(written.GetError()) : total;
            }

            [Procedure]
            public static async ValueTask<Result> StartMatch(RhinoCtx ctx, string sessionKey, int matchId, CancellationToken ct) {
                ct.ThrowIfCancellationRequested();
                return await ctx.BeginTx(sessionKey, static (db, tx, id) => { tx.Instant.Match.Insert(new Match(id)); return Result.Ok(); }, matchId);
            }

            [Procedure]
            public static async Task<Result<long>> Roll(RhinoCtx ctx) {
                await Task.Yield();
                return ctx.Random.Next(long.MaxValue);
            }

            [Procedure]
            public static Task<Result> Fail(RhinoCtx ctx) => Task.FromResult(Result.Error(Knobs.NextError));

            [Procedure]
            public static Task<Result> Throw(RhinoCtx ctx) => throw new InvalidOperationException("procedure bug, thrown synchronously");

            [Procedure]
            public static async Task<Result> ThrowAfterAwait(RhinoCtx ctx) {
                await Task.Yield();
                throw new InvalidOperationException("procedure bug, thrown after an await");
            }
        }

        public static class TestHelpers {
            public static int Unrecoverable;
            public static List<ProcedureFault> Faults = new();
            public static Session Session = new Session(ConnectionId.NewId(), new Identity(new PrincipalId("player-1")), appVersion: 3);

            public static async Task<RhinoHost> Build(string dir) {
                Unrecoverable = 0;
                Faults = new();
                return (await RhinoHostBuilder.Create(dir)
                    .AddGeneratedChildDatabases()
                    .AddGeneratedProcedures()
                    .OnUnrecoverableError(UnrecoverableErrorPolicy.Callback(_ => Interlocked.Increment(ref Unrecoverable)))
                    .OnProcedureFault(fault => { lock (Faults) Faults.Add(fault); })
                    .AddDatabase<RootDb, RootDbTransaction>(o => o.CreateDb = cold => new RootDb(cold))
                    .BuildAsync()).Unwrap();
            }

            static Task<Result<ReadOnlyMemory<byte>>> Call(RhinoHost host, uint hash, byte[] body) =>
                host.DispatchProcedureAsync(hash, Session, body, CancellationToken.None);

            static async Task<Result<T>> Call<T>(RhinoHost host, uint hash, byte[] body, Func<ReadOnlyMemory<byte>, T> decode) {
                var reply = await Call(host, hash, body);
                return reply.IsError() ? Result<T>.Error(reply.GetError()) : decode(reply.Unwrap());
            }

            public static async Task<Result> Deposit(RhinoHost host, int id, int coins) =>
                (await Call(host, GeneratedProcedures.TestNs_Procs_Deposit.Hash, GeneratedProcedures.TestNs_Procs_Deposit.EncodeArgs(id, coins))).Void();

            public static Task<Result<int>> Balance(RhinoHost host, int id) =>
                Call(host, GeneratedProcedures.TestNs_Procs_Balance.Hash, GeneratedProcedures.TestNs_Procs_Balance.EncodeArgs(id), GeneratedProcedures.TestNs_Procs_Balance.DecodeResult);

            public static async Task<Result> DepositThenFail(RhinoHost host, int id) =>
                (await Call(host, GeneratedProcedures.TestNs_Procs_DepositThenFail.Hash, GeneratedProcedures.TestNs_Procs_DepositThenFail.EncodeArgs(id))).Void();

            public static async Task<Result> DepositThenThrow(RhinoHost host, int id) =>
                (await Call(host, GeneratedProcedures.TestNs_Procs_DepositThenThrow.Hash, GeneratedProcedures.TestNs_Procs_DepositThenThrow.EncodeArgs(id))).Void();

            public static async Task<Result> WhoAmI(RhinoHost host, int probeId) =>
                (await Call(host, GeneratedProcedures.TestNs_Procs_WhoAmI.Hash, GeneratedProcedures.TestNs_Procs_WhoAmI.EncodeArgs(probeId))).Void();

            public static async Task<Result> RollInTx(RhinoHost host, int probeId) =>
                (await Call(host, GeneratedProcedures.TestNs_Procs_RollInTx.Hash, GeneratedProcedures.TestNs_Procs_RollInTx.EncodeArgs(probeId))).Void();

            public static async Task<string> ProbeText(RhinoHost host, int id) =>
                (await new RhinoCtx(host, Identity.System).BeginTx((db, tx) => Result.Ok(tx.Instant.Probe.Primary.Find(id).Get().Unwrap().Text))).Unwrap();

            public static async Task<long> ProbeNumber(RhinoHost host, int id) =>
                (await new RhinoCtx(host, Identity.System).BeginTx((db, tx) => Result.Ok(tx.Instant.Probe.Primary.Find(id).Get().Unwrap().Number))).Unwrap();

            public static Task<Result<long>> Roll(RhinoHost host) =>
                Call(host, GeneratedProcedures.TestNs_Procs_Roll.Hash, GeneratedProcedures.TestNs_Procs_Roll.EncodeArgs(), GeneratedProcedures.TestNs_Procs_Roll.DecodeResult);

            public static async Task<Result> List(RhinoHost host, int id, int price) =>
                (await Call(host, NameHashOf("market.list"), GeneratedProcedures.TestNs_Procs_List.EncodeArgs(id, price))).Void();

            public static Task<Result<int>> AddBonus(RhinoHost host, int id, int bonus) =>
                Call(host, GeneratedProcedures.TestNs_Procs_AddBonus.Hash, GeneratedProcedures.TestNs_Procs_AddBonus.EncodeArgs(id, bonus), GeneratedProcedures.TestNs_Procs_AddBonus.DecodeResult);

            public static async Task<Result> StartMatch(RhinoHost host, string key, int id) =>
                (await Call(host, GeneratedProcedures.TestNs_Procs_StartMatch.Hash, GeneratedProcedures.TestNs_Procs_StartMatch.EncodeArgs(key, id))).Void();

            public static async Task<string> Echo(RhinoHost host) {
                var id = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
                var body = GeneratedProcedures.TestNs_Procs_Echo.EncodeArgs(new Money(100, "EUR"), new[] { 1, 2, 3 }, new[] { "a", "b" }, Color.Blue, id, "hi");
                var reply = await Call(host, GeneratedProcedures.TestNs_Procs_Echo.Hash, body, GeneratedProcedures.TestNs_Procs_Echo.DecodeResult);
                var money = reply.Unwrap();
                return $"{money.Amount}|{money.Currency}";
            }

            public static async Task<Result> CallRaw(RhinoHost host, string procedure, byte[] body) =>
                (await Call(host, NameHashOf(procedure), body)).Void();

            public static async Task<bool> WalletExists(RhinoHost host, int id) =>
                (await new RhinoCtx(host, Identity.System).BeginTx((db, tx) => Result.Ok(tx.Instant.Wallet.Primary.Find(id).HasRow()))).Unwrap();

            public static async Task<bool> ListingExists(RhinoHost host, int id) =>
                (await new RhinoCtx(host, Identity.System).BeginTx((db, tx) => Result.Ok(tx.Instant.Listing.Primary.Find(id).HasRow()))).Unwrap();

            public static async Task<bool> MatchExists(RhinoHost host, string key, int id) =>
                (await new RhinoCtx(host, Identity.System).BeginTx(key, (db, tx) => Result.Ok(tx.Instant.Match.Primary.Find(id).HasRow()))).Unwrap();

            static uint NameHashOf(string name) => RhinoDB.SchemaContracts.NameHash.Compute(name);
        }
        """;

    private string dir = "";
    private Assembly asm = null!;
    private RhinoHost host = null!;

    [OneTimeSetUp]
    public void Compile() => (asm, _) = GeneratorTestHost.CompileAndLoad(Source);

    [SetUp]
    public async Task SetUp() {
        dir = Path.Combine(Path.GetTempPath(), "rhinodb-procedure-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(
            new RhinoDbConfig { Host = new HostConfig { ColdPath = dir, HttpPort = 0, HttpEnabled = false, Mode = "run" }, Server = new ServerConfig { Version = "1.2.3" } },
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(dir, GeneratorConfigLoader.ConfigFileName), json);
        host = await (Task<RhinoHost>)Helper("Build", dir)!;
    }

    [TearDown]
    public void TearDown() {
        var root = host.GetDatabase<object>();
        var cold = (ColdStore?)root.GetType().GetProperty("Cold", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(root);
        host.Dispose();
        cold?.Dispose();
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private object? Helper(string method, params object?[] args) => GeneratorTestHost.InvokeHelper(asm, "TestNs.TestHelpers", method, args);

    private async Task<T> Call<T>(string method, params object?[] args) => await (Task<T>)Helper(method, [host, .. args])!;

    private int UnrecoverableCount => (int)asm.GetType("TestNs.TestHelpers")!.GetField("Unrecoverable")!.GetValue(null)!;

    private List<ProcedureFault> Faults => (List<ProcedureFault>)asm.GetType("TestNs.TestHelpers")!.GetField("Faults")!.GetValue(null)!;

    private void SetNextError(DbError error) => asm.GetType("TestNs.Knobs")!.GetField("NextError")!.SetValue(null, error);

    // ---- registration and metadata ----

    [Test]
    public void EveryProcedure_IsRegistered_WithItsNameHashAndShape() {
        var byName = host.Procedures.ToDictionary(p => p.Name);

        Assert.Multiple(() => {
            Assert.That(byName["Deposit"].Hash, Is.EqualTo(NameHash.Compute("Deposit")));
            Assert.That(byName["Deposit"].SingleTransaction, Is.True, "the transaction-only shape is recorded as one atomic transaction.");
            Assert.That(byName["AddBonus"].SingleTransaction, Is.False);
            Assert.That(byName["Balance"].SingleTransaction, Is.False);
            Assert.That(byName.ContainsKey("market.list"), Is.True, "Name overrides the method name for routing.");
            Assert.That(byName.ContainsKey("List"), Is.False);
        });
    }

    // ---- transaction-only shape ----

    [Test]
    public async Task ATransactionProcedure_WritesThroughItsContextsTransaction() {
        Assert.That((await Call<Result>("Deposit", 1, 50)).IsOk(), Is.True);

        Assert.That((await Call<Result<int>>("Balance", 1)).Unwrap(), Is.EqualTo(50));
    }

    [Test]
    public async Task ATransactionProcedure_ReturningAnError_RollsBackItsWrites_AndTheClientGetsTheCustomCode() {
        var result = await Call<Result>("DepositThenFail", 2);

        Assert.That(result.GetError().Kind, Is.EqualTo(ErrorKind.Custom));
        Assert.That(result.GetError().CustomCode, Is.EqualTo(7));
        Assert.That(await Call<bool>("WalletExists", 2), Is.False, "one atomic transaction - the insert before the error is gone.");
    }

    [Test]
    public async Task ATransactionProcedure_ThatThrows_RollsBack_IsProcedureFailed_AndIsLoggedOnce() {
        var result = await Call<Result>("DepositThenThrow", 3);

        Assert.That(result.GetError().Kind, Is.EqualTo(ErrorKind.ProcedureFailed));
        Assert.That(await Call<bool>("WalletExists", 3), Is.False);
        Assert.That(Faults, Has.Count.EqualTo(1));
        Assert.That(Faults[0].ProcedureName, Is.EqualTo("DepositThenThrow"));
        Assert.That(Faults[0].Exception.Message, Is.EqualTo("procedure bug"));
    }

    [Test]
    public async Task TheTransactionContext_CarriesTheSession_IdentityAndServerVersion_WithAUtcTimestamp() {
        Assert.That((await Call<Result>("WhoAmI", 1)).IsOk(), Is.True);

        Assert.That(await Call<string>("ProbeText", 1), Is.EqualTo($"player-1|3|{ServerVersionParser.Parse("1.2.3")}|Utc"));
    }

    [Test]
    public async Task TheTimestamp_IsTakenWhenTheTransactionRuns() {
        var before = DateTime.UtcNow.Ticks;
        Assert.That((await Call<Result>("WhoAmI", 2)).IsOk(), Is.True);
        var after = DateTime.UtcNow.Ticks;

        Assert.That(await Call<long>("ProbeNumber", 2), Is.InRange(before, after));
    }

    [Test]
    public async Task ASingletonChild_TakesTheTransactionShape_AndNameRoutesIt() {
        Assert.That((await Call<Result>("List", 10, 99)).IsOk(), Is.True);

        Assert.That(await Call<bool>("ListingExists", 10), Is.True);
    }

    [Test]
    public async Task CustomTypes_Arrays_Enums_Guids_AndStrings_RoundTripThroughTheRawCodec_IncludingTheReturnValue() {
        var echoed = await Call<string>("Echo");

        // 100 + (1 + 2 + 3) + 2 tags + Color.Blue (2)
        Assert.That(echoed, Is.EqualTo("110|EUR:0f8fad5b-d9cb-469f-a165-70867728950e:hi"));
    }

    [Test]
    public async Task EachTransaction_GetsItsOwnRandomSeed() {
        var rolls = new HashSet<long>();
        for (var i = 0; i < 20; i++) {
            Assert.That((await Call<Result>("RollInTx", 100 + i)).IsOk(), Is.True);
            rolls.Add(await Call<long>("ProbeNumber", 100 + i));
        }

        Assert.That(rolls.Count, Is.GreaterThan(15), "a fresh unpredictable seed per transaction - not one shared sequence, not a constant.");
    }

    // ---- general shape ----

    [Test]
    public async Task AGeneralProcedure_RunsSeveralTransactions_WithArgsLambdas() {
        await Call<Result>("Deposit", 4, 10);

        Assert.That((await Call<Result<int>>("AddBonus", 4, 5)).Unwrap(), Is.EqualTo(15));
        Assert.That((await Call<Result<int>>("Balance", 4)).Unwrap(), Is.EqualTo(15));
    }

    [Test]
    public async Task AGeneralProcedure_ReachesAKeyedChild_ByKey_AndTakesACancellationToken() {
        Assert.That((await Call<Result>("StartMatch", "s1", 77)).IsOk(), Is.True);

        Assert.That(await Call<bool>("MatchExists", "s1", 77), Is.True);
    }

    [Test]
    public async Task AGeneralProcedures_Random_IsSeededPerRequest() {
        var a = (await Call<Result<long>>("Roll")).Unwrap();
        var b = (await Call<Result<long>>("Roll")).Unwrap();

        Assert.That(a, Is.Not.EqualTo(b));
    }

    // ---- request faults ----

    [Test]
    public async Task AnUnknownProcedure_IsUnknownProcedure() {
        var result = await Call<Result>("CallRaw", "NoSuchProcedure", Array.Empty<byte>());

        Assert.That(result.GetError().Kind, Is.EqualTo(ErrorKind.UnknownProcedure));
    }

    [Test]
    public async Task ArgsThatDontDecode_AreProcedureArgsInvalid_AndTheProcedureNeverRuns() {
        var result = await Call<Result>("CallRaw", "Deposit", new byte[] { 1 });

        Assert.That(result.GetError().Kind, Is.EqualTo(ErrorKind.ProcedureArgsInvalid));
        Assert.That(Faults.Single().Kind, Is.EqualTo(ErrorKind.ProcedureArgsInvalid));
    }

    [TestCase("Throw")]
    [TestCase("ThrowAfterAwait")]
    public async Task AGeneralProcedure_ThatThrows_IsProcedureFailed_AndLeavesTheHostServing(string procedure) {
        var result = await Call<Result>("CallRaw", procedure, Array.Empty<byte>());

        Assert.That(result.GetError().Kind, Is.EqualTo(ErrorKind.ProcedureFailed));
        Assert.That(Faults, Has.Count.EqualTo(1));
        Assert.That((await Call<Result>("Deposit", 5, 1)).IsOk(), Is.True, "a procedure bug is that request's problem, nobody else's.");
        Assert.That(UnrecoverableCount, Is.Zero);
    }

    // ---- the error contract: only the engine decides what is unrecoverable ----

    static private IEnumerable<ErrorKind> EveryErrorKind() =>
        Enum.GetValues<ErrorKind>().Where(k => k is not ErrorKind.None and not ErrorKind.SystemFailure and not ErrorKind.Custom);

    static private DbError ErrorOf(ErrorKind kind) =>
        (DbError)typeof(DbError).GetMethod(kind.ToString(), BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [null])!;

    [Test]
    public async Task ReturningAnyErrorKind_FromEitherShape_IsThatRequestsOutcome_AndNeverUnrecoverable() {
        var kinds = EveryErrorKind().ToList();
        Assert.That(kinds, Does.Contain(ErrorKind.WalDurabilityFailed).And.Contain(ErrorKind.ApplyFailed).And.Contain(ErrorKind.MultiTxOutcomeUnknown));

        foreach (var kind in kinds) {
            SetNextError(ErrorOf(kind));

            var general = await Call<Result>("CallRaw", "Fail", Array.Empty<byte>());
            var transaction = await Call<Result>("CallRaw", "FailInTx", Array.Empty<byte>());

            Assert.That(general.GetError().Kind, Is.EqualTo(kind), $"general shape, {kind}");
            Assert.That(transaction.GetError().Kind, Is.EqualTo(kind), $"transaction shape, {kind}");
        }

        await Task.Delay(100);
        Assert.That(UnrecoverableCount, Is.Zero, "a returned kind is data - nothing inspects it to judge the engine's health.");
        Assert.That((await Call<Result>("Deposit", 6, 1)).IsOk(), Is.True, "the Root was not poisoned by any of them.");
    }

    [Test]
    public async Task ReturningACustomError_PassesTheCodeThrough() {
        SetNextError(DbError.Custom(4242));

        var result = await Call<Result>("CallRaw", "Fail", Array.Empty<byte>());

        Assert.That(result.GetError().CustomCode, Is.EqualTo(4242));
        Assert.That(Faults, Is.Empty, "a returned error is an outcome, not a fault - nothing is logged.");
    }
}
