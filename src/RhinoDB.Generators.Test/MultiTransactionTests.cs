using System.Reflection;
using System.Text.Json;

using RhinoDB.Core;
using RhinoDB.Lib.Cold;
using RhinoDB.Lib.Durability;
using RhinoDB.Lib.Execution;
using RhinoDB.Lib.Hosting;
using RhinoDB.SchemaContracts;

namespace RhinoDB.Generators.Test;

// PlannedMultiTx end to end: real generated Root + Child databases (real undo journals, real WALs, real
// chains.log), driven through a real RhinoHost. Every chain body lives in the dynamically-compiled source's
// TestHelpers - the Root/Child/row types only exist there - and is a static, capture-free lambda taking its
// args explicitly, which is the shape the chain API is built around.
public class MultiTransactionTests {
    private const string Source = """
        using MemoryPack;
        using MessagePack;
        using RhinoDB.Core;
        using RhinoDB.Core.Tables;
        using RhinoDB.Lib.Execution;
        using RhinoDB.Lib.Hosting;
        using RhinoDB.Lib.Realtime;
        using System.Linq;
        using System.Threading.Tasks;

        namespace TestNs;

        [Database]
        public partial class RootDb : DbContext<RootDbTransaction> { }

        [ChildDatabase<RootDb, string>]
        public partial class SessionDb : DbContext<SessionDbTransaction> { }

        [Table<RootDb>(TableKind.Persistent, RingBufferCapacity = 64)]
        [MemoryPackable]
        [MessagePackObject]
        public readonly partial record struct Wallet([PrimaryKey] [property: Key(0)] int Id, [property: Key(1)] int Coins);

        [Table<SessionDb>(TableKind.Persistent)]
        [MemoryPackable]
        [MessagePackObject]
        public readonly partial record struct Match([PrimaryKey] [property: Key(0)] int Id, [property: Key(1)] int Score);

        [Table<SessionDb>(TableKind.Instant)]
        [MemoryPackable]
        [MessagePackObject]
        public readonly partial record struct Presence([PrimaryKey] [property: Key(0)] int Id);

        public static class TestHelpers {
            public const string Session = "s1";

            public static async Task<RhinoHost> Build(string dir) {
                var host = (await RhinoHostBuilder.Create(dir)
                    .AddGeneratedChildDatabases()
                    .OnUnrecoverableError(UnrecoverableErrorPolicy.Callback(static _ => { }))
                    .AddDatabase<RootDb, RootDbTransaction>(o => o.CreateDb = cold => new RootDb(cold))
                    .BuildAsync()).Unwrap();
                await new RootDbLoader().LoadAsync(host.GetDatabase<RootDb>());
                (await host.GetOrActivateChildAsync<SessionDb, SessionDbTransaction, string>(Session)).ThrowIfError();
                return host;
            }

            // Replay mode: nothing is loaded from libmdbx - memory is rebuilt purely from the WAL history.
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

            // Values from both a Root and a Child body, plus whether reading one before Commit was refused.
            public static async Task<(bool Committed, bool ReadBeforeCommitThrew, int RootValue, string ChildValue)> CommitWithValues(
                RhinoHost host, int walletId, int matchId) {
                var chain = Ctx(host).PlanMultiTx();
                var coins = chain.Add(
                    static (db, tx, id) => { tx.Wallet.Insert(new Wallet(id, id * 10)); return Result.Ok(id * 10); }, walletId);
                var label = chain.Add(
                    Session, static (db, tx, id) => { tx.Match.Insert(new Match(id, 1)); return Result.Ok("match-" + id); }, matchId);

                var readBeforeCommitThrew = false;
                try { _ = coins.Value; } catch (RhinoDB.Core.Exceptions.MultiTxValueNotProducedException) { readBeforeCommitThrew = true; }

                var committed = (await chain.Commit()).IsOk();
                return (committed, readBeforeCommitThrew, coins.Value, label.Value);
            }

            public static async Task<(bool Committed, bool HasValue, bool ReadThrew)> FailWithValue(RhinoHost host, int walletId) {
                var chain = Ctx(host).PlanMultiTx();
                var coins = chain.Add(
                    static (db, tx, id) => { tx.Wallet.Insert(new Wallet(id, 1)); return Result.Ok(id); }, walletId);
                chain.Add(Session, static (db, tx) => Result.Error(DbError.Custom(3)));

                var committed = (await chain.Commit()).IsOk();
                var readThrew = false;
                try { _ = coins.Value; } catch (System.InvalidOperationException) { readThrew = true; }
                return (committed, coins.HasValue, readThrew);
            }

            // Settling a session in one transaction: the child body reads the match and marks it, the Root body
            // records the result using the child's value - Child -> Root, which Root-first execution couldn't do.
            public static async Task<(Result Committed, int Coins)> SettleMatchIntoWallet(RhinoHost host, int matchId, int walletId) {
                var multi = Ctx(host).PlanMultiTx();
                var score = multi.Add(Session, static (db, tx, id) => {
                    if (!tx.Match.Primary.Find(id).HasRow()) return Result<int>.Error(DbError.IndexKeyNotFound());
                    tx.Match.Update(id, new Match(id, -1));
                    return Result.Ok(id * 7);
                }, matchId);
                multi.Add(static (db, tx, s) => { tx.Wallet.Insert(new Wallet(s.WalletId, s.Score.Value)); return Result.Ok(); },
                    (WalletId: walletId, Score: score));
                var committed = await multi.Commit();
                return (committed, committed.IsOk() ? score.Value : 0);
            }

            // Child -> Root -> Child: each body builds on the previous one's value, proving bodies run in Add order.
            public static async Task<int> PingPong(RhinoHost host, int matchId, int walletId) {
                var multi = Ctx(host).PlanMultiTx();
                var first = multi.Add(Session, static (db, tx) => Result.Ok(1));
                var second = multi.Add(static (db, tx, prev) => { tx.Wallet.Insert(new Wallet(prev.Id, prev.First.Value + 1)); return Result.Ok(prev.First.Value + 1); },
                    (Id: walletId, First: first));
                var third = multi.Add(Session, static (db, tx, prev) => { tx.Match.Insert(new Match(prev.Id, prev.Second.Value + 1)); return Result.Ok(prev.Second.Value + 1); },
                    (Id: matchId, Second: second));
                (await multi.Commit()).ThrowIfError();
                return third.Value;
            }

            // Reading a value whose body was added later - it hasn't run yet.
            public static async Task<Result> ConsumeBeforeProduced(RhinoHost host, int walletId) {
                var multi = Ctx(host).PlanMultiTx();
                var later = new TxValue<int>[1];
                multi.Add(static (db, tx, s) => { tx.Wallet.Insert(new Wallet(s.Id, s.Later[0].Value)); return Result.Ok(); }, (Id: walletId, Later: later));
                later[0] = multi.Add(Session, static (db, tx) => Result.Ok(5));
                return await multi.Commit();
            }

            public static async Task<(bool Committed, bool HasValue, int MatchScoreAfter)> ValueThenLaterFailure(RhinoHost host, int matchId) {
                var multi = Ctx(host).PlanMultiTx();
                var produced = multi.Add(Session, static (db, tx, id) => { tx.Match.Update(id, new Match(id, 99)); return Result.Ok(id); }, matchId);
                multi.Add(static (db, tx) => Result.Error(DbError.Custom(4)));
                var committed = (await multi.Commit()).IsOk();
                var score = (await Ctx(host).BeginTx(Session, (db, tx) => {
                    var row = tx.Match.Primary.Find(matchId);
                    return Result.Ok(row.HasRow() ? row.Get().Unwrap().Score : int.MinValue);
                })).Unwrap();
                return (committed, produced.HasValue, score);
            }

            public static async Task<string> RootOnlyValueWithoutArgs(RhinoHost host) {
                var chain = Ctx(host).PlanMultiTx();
                var label = chain.Add(static (db, tx) => Result.Ok("no-args"));
                await chain.Commit();
                return label.Value;
            }

            static PlannedMultiTx WalletAndMatch(RhinoHost host, int walletId, int matchId) =>
                Ctx(host).PlanMultiTx()
                    .Add(static (db, tx, id) => { tx.Wallet.Insert(new Wallet(id, 100)); return Result.Ok(); }, walletId)
                    .Add(Session, static (db, tx, id) => { tx.Match.Insert(new Match(id, 3)); return Result.Ok(); }, matchId);

            public static Task<Result> CommitWalletAndMatch(RhinoHost host, int walletId, int matchId) =>
                WalletAndMatch(host, walletId, matchId).Commit();

            public static Task<Result> CommitWalletAndMatchThenCrash(RhinoHost host, int walletId, int matchId, MultiTxCrashPoint crashAt) {
                var chain = WalletAndMatch(host, walletId, matchId);
                chain.TestOnlySimulateCrashAt = crashAt;
                return chain.Commit();
            }

            // Child is added first on purpose: participants are still taken in fixed order (Root, then Children),
            // never Add order - that's what makes two chains touching the same databases deadlock-free.
            public static Task<Result> CommitMatchThenWallet(RhinoHost host, int walletId, int matchId) =>
                Ctx(host).PlanMultiTx()
                    .Add(Session, static (db, tx, id) => { tx.Match.Insert(new Match(id, 1)); return Result.Ok(); }, matchId)
                    .Add(static (db, tx, id) => { tx.Wallet.Insert(new Wallet(id, 1)); return Result.Ok(); }, walletId)
                    .Commit();

            public static Task<Result> CommitWalletThenFailingChildBody(RhinoHost host, int walletId) =>
                Ctx(host).PlanMultiTx()
                    .Add(static (db, tx, id) => { tx.Wallet.Insert(new Wallet(id, 100)); return Result.Ok(); }, walletId)
                    .Add(Session, static (db, tx) => Result.Error(DbError.Custom(7)))
                    .Commit();

            public static Task<Result> CommitTwoRootSteps(RhinoHost host, int firstId, int secondId) =>
                Ctx(host).PlanMultiTx()
                    .Add<RootDb, RootDbTransaction, int>(static (db, tx, id) => { tx.Wallet.Insert(new Wallet(id, 1)); return Result.Ok(); }, firstId)
                    .Add<RootDb, RootDbTransaction, int>(static (db, tx, id) => { tx.Wallet.Insert(new Wallet(id, 2)); return Result.Ok(); }, secondId)
                    .Commit();

            public static Task<Result> RollbackThenCommit(RhinoHost host, int walletId) {
                var chain = Ctx(host).PlanMultiTx()
                    .Add(static (db, tx, id) => { tx.Wallet.Insert(new Wallet(id, 1)); return Result.Ok(); }, walletId);
                chain.Rollback();
                return chain.Commit();
            }

            public static bool ChildAddFromAHookCtxThrows(RhinoHost host) {
                var hookCtx = new RhinoCtx(host.GetDatabase<RootDb>(), Identity.System);
                try {
                    hookCtx.PlanMultiTx().Add(Session, static (db, tx) => Result.Ok());
                    return false;
                } catch (RhinoDB.Core.Exceptions.ChildDatabaseRequiresHostException) { return true; }
            }

            // Result-returning APIs from a hook's ctx: the missing host comes back as an error, never a throw.
            public static async Task<(Result BeginTx, Result LockMultiTx)> ChildFromAHookCtxThroughResultApis(RhinoHost host) {
                var hookCtx = new RhinoCtx(host.GetDatabase<RootDb>(), Identity.System);
                var beginTx = await hookCtx.BeginTx(Session, static (db, tx) => Result.Ok());
                var open = await hookCtx.LockMultiTx(p => p.SessionDb(Session));
                return (beginTx, open.IsError() ? Result.Error(open.GetError()) : Result.Ok());
            }

            public static async Task<Result> OpenMultiTxDeclaringNothing(RhinoHost host) {
                var open = await Ctx(host).LockMultiTx(static p => { });
                return open.IsError() ? Result.Error(open.GetError()) : Result.Ok();
            }

            // The child's share touches only an Instant table, so the root is the chain's one durable participant.
            public static Task<Result> CommitWalletAndPresence(RhinoHost host, int walletId, int presenceId) =>
                Ctx(host).PlanMultiTx()
                    .Add(static (db, tx, id) => { tx.Wallet.Insert(new Wallet(id, 1)); return Result.Ok(); }, walletId)
                    .Add(Session, static (db, tx, id) => { tx.Instant.Presence.Insert(new Presence(id)); return Result.Ok(); }, presenceId)
                    .Commit();

            public static async Task<bool> PresenceExists(RhinoHost host, int id) =>
                (await Ctx(host).BeginTx(Session, (db, tx) => Result.Ok(tx.Instant.Presence.Primary.Find(id).HasRow()))).Unwrap();

            // ---- Locked (LockMultiTx): each Run executes now and hands its value straight back ----

            public static async Task<(Result Committed, int Score)> LockedSettle(RhinoHost host, int matchId, int walletId) {
                await using var open = (await Ctx(host).LockMultiTx(p => p.RootDb().SessionDb(Session))).Unwrap();
                var score = (await open.Run(Session, static (db, t, id) => Result.Ok(t.Match.Primary.Find(id).HasRow() ? id * 3 : -1), matchId)).Unwrap();
                var coins = score + 1;      // plain code between steps
                await Task.Yield();         // and an await - allowed; the databases simply stay held meanwhile
                (await open.Run(static (db, t, s) => { t.Wallet.Insert(new Wallet(s.Id, s.Coins)); return Result.Ok(); }, (Id: walletId, Coins: coins))).ThrowIfError();
                return (await open.Commit(), score);
            }

            public static async Task<(bool ReadFinishedWhileHeld, bool ReadSawTheRow)> HoldsTheRootUntilCommit(RhinoHost host, int walletId) {
                var open = (await Ctx(host).LockMultiTx(p => p.RootDb())).Unwrap();
                (await open.Run(static (db, t, id) => { t.Wallet.Insert(new Wallet(id, 1)); return Result.Ok(); }, walletId)).ThrowIfError();
                var read = Task.Run(() => WalletExists(host, walletId));
                await Task.Delay(200);
                var finishedWhileHeld = read.IsCompleted;
                (await open.Commit()).ThrowIfError();
                return (finishedWhileHeld, await read);
            }

            public static async Task<(Result Step, Result Commit)> LockedStepFailure(RhinoHost host, int walletId) {
                var open = (await Ctx(host).LockMultiTx(p => p.RootDb().SessionDb(Session))).Unwrap();
                (await open.Run(static (db, t, id) => { t.Wallet.Insert(new Wallet(id, 1)); return Result.Ok(); }, walletId)).ThrowIfError();
                var step = await open.Run(Session, static (db, t) => Result.Error(DbError.Custom(9)));
                return (step, await open.Commit());
            }

            public static async Task<Result> LockedUndeclared(RhinoHost host) {
                await using var open = (await Ctx(host).LockMultiTx(p => p.RootDb())).Unwrap();
                return await open.Run(Session, static (db, t) => Result.Ok());
            }

            public static async Task LockedDisposeWithoutCommit(RhinoHost host, int walletId) {
                await using var open = (await Ctx(host).LockMultiTx(p => p.RootDb())).Unwrap();
                (await open.Run(static (db, t, id) => { t.Wallet.Insert(new Wallet(id, 1)); return Result.Ok(); }, walletId)).ThrowIfError();
            }

            public static async Task<(Result Run, Result Commit)> LockedAfterCommit(RhinoHost host) {
                var open = (await Ctx(host).LockMultiTx(p => p.RootDb())).Unwrap();
                await open.Commit();
                return (await open.Run(static (db, t) => Result.Ok()), await open.Commit());
            }

            // Declared (and used) in opposite orders - holding still happens in the fixed global order.
            public static async Task<Result> LockedBothWays(RhinoHost host, int id, bool childFirst) {
                var open = (childFirst
                    ? await Ctx(host).LockMultiTx(p => p.SessionDb(Session).RootDb())
                    : await Ctx(host).LockMultiTx(p => p.RootDb().SessionDb(Session))).Unwrap();
                if (childFirst) (await open.Run(Session, static (db, t, i) => { t.Match.Insert(new Match(i, 1)); return Result.Ok(); }, id)).ThrowIfError();
                (await open.Run(static (db, t, i) => { t.Wallet.Insert(new Wallet(i, 1)); return Result.Ok(); }, id)).ThrowIfError();
                if (!childFirst) (await open.Run(Session, static (db, t, i) => { t.Match.Insert(new Match(i, 1)); return Result.Ok(); }, id)).ThrowIfError();
                return await open.Commit();
            }

            public static async Task<Result> LockedThenCrash(RhinoHost host, int walletId, int matchId, MultiTxCrashPoint crashAt) {
                var open = (await Ctx(host).LockMultiTx(p => p.RootDb().SessionDb(Session))).Unwrap();
                open.TestOnlySimulateCrashAt = crashAt;
                (await open.Run(static (db, t, id) => { t.Wallet.Insert(new Wallet(id, 1)); return Result.Ok(); }, walletId)).ThrowIfError();
                (await open.Run(Session, static (db, t, id) => { t.Match.Insert(new Match(id, 1)); return Result.Ok(); }, matchId)).ThrowIfError();
                return await open.Commit();
            }

            // ---- Per-step apply: each step's writes land in the tables at once - visible to the same transaction's
            // later steps on that database, hidden from everyone else by the lock until Commit ----

            public static async Task<(bool SawInsert, Result Committed)> LockedReadsItsOwnInsert(RhinoHost host, int walletId) {
                var open = (await Ctx(host).LockMultiTx(p => p.RootDb())).Unwrap();
                (await open.Run(static (db, t, id) => { t.Wallet.Insert(new Wallet(id, 5)); return Result.Ok(); }, walletId)).ThrowIfError();
                var saw = (await open.Run(static (db, t, id) => Result.Ok(t.Wallet.Primary.Find(id).HasRow()), walletId)).Unwrap();
                return (saw, await open.Commit());
            }

            public static async Task<(int ScoreSeen, Result Committed)> LockedReadsItsOwnUpdate(RhinoHost host, int matchId) {
                var open = (await Ctx(host).LockMultiTx(p => p.SessionDb(Session))).Unwrap();
                (await open.Run(Session, static (db, t, id) => { t.Match.Update(id, new Match(id, 50)); return Result.Ok(); }, matchId)).ThrowIfError();
                var seen = (await open.Run(Session, static (db, t, id) => Result.Ok(ScoreOf(t, id)), matchId)).Unwrap();
                return (seen, await open.Commit());
            }

            public static async Task<(bool SeenAfterDelete, Result Committed)> LockedReadsItsOwnDelete(RhinoHost host, int matchId) {
                var open = (await Ctx(host).LockMultiTx(p => p.SessionDb(Session))).Unwrap();
                (await open.Run(Session, static (db, t, id) => { t.Match.Delete(id); return Result.Ok(); }, matchId)).ThrowIfError();
                var seen = (await open.Run(Session, static (db, t, id) => Result.Ok(t.Match.Primary.Find(id).HasRow()), matchId)).Unwrap();
                return (seen, await open.Commit());
            }

            // Inserts, two updates of one row and a delete, spread over both databases - then nothing commits.
            public static async Task LockedSeveralRunsThenRollback(RhinoHost host, int firstWallet, int secondWallet, int updatedMatch, int deletedMatch) {
                var open = (await Ctx(host).LockMultiTx(p => p.RootDb().SessionDb(Session))).Unwrap();
                (await open.Run(static (db, t, id) => { t.Wallet.Insert(new Wallet(id, 1)); return Result.Ok(); }, firstWallet)).ThrowIfError();
                (await open.Run(Session, static (db, t, id) => { t.Match.Update(id, new Match(id, 77)); return Result.Ok(); }, updatedMatch)).ThrowIfError();
                (await open.Run(static (db, t, id) => { t.Wallet.Insert(new Wallet(id, 2)); return Result.Ok(); }, secondWallet)).ThrowIfError();
                (await open.Run(Session, static (db, t, id) => { t.Match.Delete(id); return Result.Ok(); }, deletedMatch)).ThrowIfError();
                (await open.Run(Session, static (db, t, id) => { t.Match.Update(id, new Match(id, 88)); return Result.Ok(); }, updatedMatch)).ThrowIfError();
                await open.Rollback();
            }

            // The failing body stages a write and then fails - that write was never applied, and must not survive
            // in the shared transaction object to be applied by whatever runs on this database next.
            public static async Task<(Result Failed, Result Commit)> LockedAppliedRunThenAFailingRunThatStagedAWrite(RhinoHost host, int appliedId, int stagedId) {
                var open = (await Ctx(host).LockMultiTx(p => p.RootDb())).Unwrap();
                (await open.Run(static (db, t, id) => { t.Wallet.Insert(new Wallet(id, 1)); return Result.Ok(); }, appliedId)).ThrowIfError();
                var failed = await open.Run(static (db, t, id) => { t.Wallet.Insert(new Wallet(id, 1)); return Result.Error(DbError.Custom(11)); }, stagedId);
                return (failed, await open.Commit());
            }

            public static async Task<(Result Second, Result Commit)> LockedDuplicateAcrossRuns(RhinoHost host, int walletId) {
                var open = (await Ctx(host).LockMultiTx(p => p.RootDb())).Unwrap();
                (await open.Run(static (db, t, id) => { t.Wallet.Insert(new Wallet(id, 1)); return Result.Ok(); }, walletId)).ThrowIfError();
                var second = await open.Run(static (db, t, id) => { t.Wallet.Insert(new Wallet(id, 2)); return Result.Ok(); }, walletId);
                return (second, await open.Commit());
            }

            public static async Task<Result> LockedThreeRootRuns(RhinoHost host, int first, int second) {
                var open = (await Ctx(host).LockMultiTx(p => p.RootDb())).Unwrap();
                (await open.Run(static (db, t, id) => { t.Wallet.Insert(new Wallet(id, 1)); return Result.Ok(); }, first)).ThrowIfError();
                (await open.Run(static (db, t, id) => { t.Wallet.Insert(new Wallet(id, 2)); return Result.Ok(); }, second)).ThrowIfError();
                (await open.Run(static (db, t, id) => { t.Wallet.Update(id, new Wallet(id, 3)); return Result.Ok(); }, first)).ThrowIfError();
                return await open.Commit();
            }

            public static async Task LockedTwoRootRunsThenRollback(RhinoHost host, int first, int second) {
                var open = (await Ctx(host).LockMultiTx(p => p.RootDb())).Unwrap();
                (await open.Run(static (db, t, id) => { t.Wallet.Insert(new Wallet(id, 1)); return Result.Ok(); }, first)).ThrowIfError();
                (await open.Run(static (db, t, id) => { t.Wallet.Insert(new Wallet(id, 2)); return Result.Ok(); }, second)).ThrowIfError();
                await open.Rollback();
            }

            // Another request queued behind the lock must never observe the applied-but-uncommitted row.
            public static async Task<(bool ReadFinishedWhileHeld, bool ReadSawTheRow)> OtherRequestBehindARolledBackLock(RhinoHost host, int walletId) {
                var open = (await Ctx(host).LockMultiTx(p => p.RootDb())).Unwrap();
                (await open.Run(static (db, t, id) => { t.Wallet.Insert(new Wallet(id, 1)); return Result.Ok(); }, walletId)).ThrowIfError();
                var read = Task.Run(() => WalletExists(host, walletId));
                await Task.Delay(200);
                var finishedWhileHeld = read.IsCompleted;
                await open.Rollback();
                return (finishedWhileHeld, await read);
            }

            public static async Task<Result> LockedSeveralRunsPerDatabaseThenCrash(
                RhinoHost host, int firstWallet, int secondWallet, int firstMatch, int secondMatch, MultiTxCrashPoint crashAt) {
                var open = (await Ctx(host).LockMultiTx(p => p.RootDb().SessionDb(Session))).Unwrap();
                open.TestOnlySimulateCrashAt = crashAt;
                (await open.Run(static (db, t, id) => { t.Wallet.Insert(new Wallet(id, 1)); return Result.Ok(); }, firstWallet)).ThrowIfError();
                (await open.Run(Session, static (db, t, id) => { t.Match.Insert(new Match(id, 1)); return Result.Ok(); }, firstMatch)).ThrowIfError();
                (await open.Run(static (db, t, id) => { t.Wallet.Insert(new Wallet(id, 2)); return Result.Ok(); }, secondWallet)).ThrowIfError();
                (await open.Run(Session, static (db, t, id) => { t.Match.Insert(new Match(id, 2)); return Result.Ok(); }, secondMatch)).ThrowIfError();
                return await open.Commit();
            }

            public static async Task<(Result Committed, bool SecondSawFirst)> PlannedLaterBodySeesAnEarlierBodysWrite(RhinoHost host, int walletId) {
                var multi = Ctx(host).PlanMultiTx();
                multi.Add(static (db, tx, id) => { tx.Wallet.Insert(new Wallet(id, 1)); return Result.Ok(); }, walletId);
                var saw = multi.Add(static (db, tx, id) => Result.Ok(tx.Wallet.Primary.Find(id).HasRow()), walletId);
                var committed = await multi.Commit();
                return (committed, committed.IsOk() && saw.Value);
            }

            public static async Task<ulong[]> WalletRingLsns(RhinoHost host) =>
                (await Ctx(host).BeginTx((db, tx) => {
                    using var entries = tx.Wallet.TryGetChangesSince(0).Unwrap();
                    var buffer = entries.Buffer();
                    var lsns = new ulong[buffer.Length];
                    for (var i = 0; i < buffer.Length; i++) lsns[i] = buffer[i].Lsn;
                    return Result.Ok(lsns);
                })).Unwrap();

            public static async Task<int> MatchScore(RhinoHost host, int matchId) =>
                (await Ctx(host).BeginTx(Session, (db, tx) => Result.Ok(ScoreOf(tx, matchId)))).Unwrap();

            public static async Task<int> MatchCount(RhinoHost host) =>
                (await Ctx(host).BeginTx(Session, (db, tx) => { using var q = tx.Match.Iter(); return Result.Ok(q.Get().Unwrap().Length); })).Unwrap();

            public static Task<Result> InsertWalletDirectly(RhinoHost host, int walletId) =>
                Ctx(host).BeginTx((db, tx) => { tx.Wallet.Insert(new Wallet(walletId, 0)); return Result.Ok(); });

            static int ScoreOf(SessionDbTransaction t, int id) {
                var row = t.Match.Primary.Find(id);
                return row.HasRow() ? row.Get().Unwrap().Score : int.MinValue;
            }

            // A restart that touches only the Root - the child stays inactive on disk.
            public static async Task<RhinoHost> BuildRootOnly(string dir) {
                var host = (await RhinoHostBuilder.Create(dir)
                    .AddGeneratedChildDatabases()
                    .OnUnrecoverableError(UnrecoverableErrorPolicy.Callback(static _ => { }))
                    .AddDatabase<RootDb, RootDbTransaction>(o => o.CreateDb = cold => new RootDb(cold))
                    .BuildAsync()).Unwrap();
                await new RootDbLoader().LoadAsync(host.GetDatabase<RootDb>());
                return host;
            }

            public static Task<Result> DisposeChild(RhinoHost host) =>
                host.DisposeChildAsync<SessionDb, SessionDbTransaction, string>(Session);

            public static async Task<object> Child(RhinoHost host) =>
                (await host.GetOrActivateChildAsync<SessionDb, SessionDbTransaction, string>(Session)).Unwrap();

            public static Task<Result> InsertMatchDirectly(RhinoHost host, int matchId) =>
                Ctx(host).BeginTx(Session, (db, tx) => { tx.Match.Insert(new Match(matchId, 0)); return Result.Ok(); });

            public static async Task<bool> WalletExists(RhinoHost host, int id) =>
                (await Ctx(host).BeginTx((db, tx) => Result.Ok(tx.Wallet.Primary.Find(id).HasRow()))).Unwrap();

            public static async Task<bool> MatchExists(RhinoHost host, int id) =>
                (await Ctx(host).BeginTx(Session, (db, tx) => Result.Ok(tx.Match.Primary.Find(id).HasRow()))).Unwrap();
        }
        """;

    private string dir = "";
    private Assembly asm = null!;

    [OneTimeSetUp]
    public void Compile() => (asm, _) = GeneratorTestHost.CompileAndLoad(Source);

    [SetUp]
    public void SetUp() {
        dir = Path.Combine(Path.GetTempPath(), "rhinodb-transaction-chain-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        WriteConfig("run");
    }

    private void WriteConfig(string mode) {
        var json = JsonSerializer.Serialize(
            new RhinoDbConfig { Host = new HostConfig { ColdPath = dir, HttpPort = 0, HttpEnabled = false, Mode = mode } },
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(dir, GeneratorConfigLoader.ConfigFileName), json);
    }

    [TearDown]
    public void TearDown() {
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private Task<RhinoHost> Build() => (Task<RhinoHost>)Helper("Build", dir)!;

    private object? Helper(string method, params object?[] args) => GeneratorTestHost.InvokeHelper(asm, "TestNs.TestHelpers", method, args);

    private async Task<Result> Call(string method, params object?[] args) => await (Task<Result>)Helper(method, args)!;

    private async Task<bool> Exists(string method, RhinoHost host, int id) => await (Task<bool>)Helper(method, host, id)!;

    static private ColdStore ColdOf(object db) =>
        (ColdStore)db.GetType().GetProperty("Cold", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(db)!;

    // A real process exit closes every store; here the Root's own ColdStore (internal, so reflected) has to be
    // closed by hand before the same directory can be reopened in-process. Children close via host.Dispose.
    static private void Shutdown(RhinoHost host) {
        var cold = ColdOf(host.GetDatabase<object>());
        host.Dispose();
        cold.Dispose();
    }

    [Test]
    public async Task RootAndChild_Commit_AppliesBothShares() {
        var host = await Build();

        var result = await Call("CommitWalletAndMatch", host, 1, 1);

        Assert.That(result.IsOk(), Is.True);
        Assert.That(await Exists("WalletExists", host, 1), Is.True);
        Assert.That(await Exists("MatchExists", host, 1), Is.True);
        Shutdown(host);
    }

    [Test]
    public async Task AChildBodyFailing_RevertsTheRootShareThatWasAlreadyApplied() {
        // Root is taken first and fully applies (undo retained) before the child's body even runs.
        var host = await Build();

        var result = await Call("CommitWalletThenFailingChildBody", host, 5);

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetError().Kind, Is.EqualTo(ErrorKind.Custom));
        Assert.That(await Exists("WalletExists", host, 5), Is.False, "the root's already-applied share must be reverted when a later participant fails.");
        Shutdown(host);
    }

    [Test]
    public async Task AChildFailingAtApplyTime_RevertsTheRootShareToo() {
        var host = await Build();
        Assert.That((await Call("InsertMatchDirectly", host, 9)).IsOk(), Is.True);

        // Match 9 already exists - the child's share fails validation inside its apply, after the root applied.
        var result = await Call("CommitWalletAndMatch", host, 9, 9);

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetError().Kind, Is.EqualTo(ErrorKind.DuplicateKey));
        Assert.That(await Exists("WalletExists", host, 9), Is.False);
        Shutdown(host);
    }

    [Test]
    public async Task RollbackBeforeCommit_AppliesNothing_AndTheChainIsSingleUse() {
        var host = await Build();

        var commit = await Call("RollbackThenCommit", host, 3);

        Assert.That(commit.GetError().Kind, Is.EqualTo(ErrorKind.MultiTxAlreadyFinished), "Commit returns a Result - single-use is reported, not thrown.");
        Assert.That(await Exists("WalletExists", host, 3), Is.False);
        Shutdown(host);
    }

    [Test]
    public async Task AChildParticipantFromAHookCtx_IsRejectedAtAddTime() {
        var host = await Build();

        Assert.That((bool)Helper("ChildAddFromAHookCtxThrows", host)!, Is.True);
        Shutdown(host);
    }

    [Test]
    public async Task ConcurrentChainsOverTheSameDatabasesInOppositeAddOrder_NeverDeadlock() {
        var host = await Build();

        var chains = Enumerable.Range(100, 40).Select(i => i % 2 == 0
            ? Call("CommitWalletAndMatch", host, i, i)
            : Call("CommitMatchThenWallet", host, i, i));
        var all = Task.WhenAll(chains);
        var finished = await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(30)));

        Assert.That(finished, Is.SameAs(all), "every chain takes Root before Children regardless of Add order - none may wait on another in a cycle.");
        Assert.That(all.Result.All(r => r.IsOk()), Is.True);
        Shutdown(host);
    }

    [Test]
    public async Task ASingleDurableParticipant_CommitsAsOnePlainWalEntry_WithNoChainRecords() {
        var host = await Build();

        Assert.That((await Call("CommitTwoRootSteps", host, 20, 21)).IsOk(), Is.True);
        Shutdown(host);

        var rootWal = WriteAheadLog.ReadEntriesShared(Path.Combine(dir, "wal.dat")).Unwrap();
        Assert.That(rootWal, Has.Length.EqualTo(1));
        Assert.That(rootWal[0].Kind, Is.EqualTo(WalEntryKind.Operation), "one durable participant needs no two-phase commit - its single WAL entry already is atomic.");
        Assert.That(rootWal[0].Changes, Has.Length.EqualTo(2), "both steps landed in that one entry.");
    }

    [Test]
    public async Task ADurableAndAnInstantOnlyParticipant_CommitTogetherThroughTheSingleEntryPath() {
        var host = await Build();

        var result = await Call("CommitWalletAndPresence", host, 70, 70);

        Assert.That(result.IsOk(), Is.True);
        Assert.That(await Exists("WalletExists", host, 70), Is.True);
        Assert.That(await Exists("PresenceExists", host, 70), Is.True);
        Shutdown(host);
        Assert.That(WriteAheadLog.ReadEntriesShared(Path.Combine(dir, "wal.dat")).Unwrap().All(e => e.Kind == WalEntryKind.Operation), Is.True,
            "an Instant-only share has nothing durable to prepare, so this never needed a two-phase commit.");
    }

    [Test]
    public async Task AFailedPrepareFsyncAfterAnotherParticipantPrepared_IsAbortedAuthoritatively_EvenAcrossARestart() {
        // The child's prepare bytes still reach its WAL file - only the fsync "fails". On restart both prepares
        // are therefore on disk, so without a recorded abort the roll-forward rule would wrongly commit a chain
        // whose caller was told it failed.
        var host = await Build();
        var childCold = ColdOf(await (Task<object>)Helper("Child", host)!);
        childCold.Wal.TestOnlyBeforeFlush = () => throw new IOException("injected fsync failure");

        var result = await Call("CommitWalletAndMatch", host, 80, 80);

        childCold.Wal.TestOnlyBeforeFlush = null;
        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetError().Kind, Is.EqualTo(ErrorKind.WalDurabilityFailed));
        // Early lock release: the databases were released before the flush failed, so the chain's writes can't be
        // reverted under whatever ran since - the participants are poisoned instead, and recovery sorts it out.
        Assert.That((await Call("InsertMatchDirectly", host, 81)).IsError(), Is.True, "the participants stop rather than run on unrecoverable state.");
        Shutdown(host);

        var childWal = WriteAheadLog.ReadEntriesShared(Path.Combine(dir, "Children", "SessionDb", "s1", "wal.dat")).Unwrap();
        Assert.That(childWal.Any(e => e.Kind == WalEntryKind.ChainPrepare), Is.True, "precondition: the 'failed' prepare is physically on disk.");

        var restarted = await Build();

        Assert.That(await Exists("WalletExists", restarted, 80), Is.False);
        Assert.That(await Exists("MatchExists", restarted, 80), Is.False);
        Shutdown(restarted);
    }

    [Test]
    public async Task DisposingAChild_RecordsItsChainsCentrallyBeforeItsWalIsDeleted() {
        // Disposal deletes the child's directory - WAL included. Its chain prepares are evidence a sibling may
        // still need, so each chain's decision must be in chains.log before the delete.
        var host = await Build();
        Assert.That((await Call("CommitWalletAndMatch", host, 90, 90)).IsOk(), Is.True);

        var disposed = await Call("DisposeChild", host);

        Assert.That(disposed.IsOk(), Is.True);
        Assert.That(Directory.Exists(Path.Combine(dir, "Children", "SessionDb", "s1")), Is.False);
        Assert.That(ChainLog.Open(dir).Unwrap().RetainedChainCount, Is.EqualTo(1), "recorded, and kept until the root consumes its own prepare too.");
        Shutdown(host);

        var restarted = await Build();
        Assert.That(await Exists("WalletExists", restarted, 90), Is.True);
        Shutdown(restarted);
        Assert.That(ChainLog.Open(dir).Unwrap().RetainedChainCount, Is.EqualTo(0), "both participants consumed the chain - compacted away.");
    }

    [Test]
    public async Task ReplayMode_AfterACrashPastEveryPrepare_ReplaysTheChainFromTheLiveWalTail() {
        // Genesis replay reads the live WAL tail directly, without recovery - so it must decide the chain prepare
        // still sitting there itself (markerless here: the crash came before any marker), not skip it.
        var host = await Build();
        await Call("CommitWalletAndMatchThenCrash", host, 100, 100, MultiTxCrashPoint.AfterAllDurablePrepares);
        Shutdown(host);
        WriteConfig("replay");

        var replayed = await (Task<RhinoHost>)Helper("BuildReplay", dir)!;

        Assert.That(await Exists("WalletExists", replayed, 100), Is.True, "every prepare was durable - replay completes the chain just like recovery does.");
        Shutdown(replayed);
    }

    [Test]
    public async Task ReplayMode_AfterACrashBeforeEveryPrepare_LeavesTheChainOut() {
        var host = await Build();
        await Call("CommitWalletAndMatchThenCrash", host, 110, 110, MultiTxCrashPoint.AfterFirstDurablePrepare);
        Shutdown(host);
        WriteConfig("replay");

        var replayed = await (Task<RhinoHost>)Helper("BuildReplay", dir)!;

        Assert.That(await Exists("WalletExists", replayed, 110), Is.False);
        Shutdown(replayed);
    }

    [Test]
    public async Task BodyValues_FromRootAndChild_AreReadableOnlyOnceTheChainCommitted() {
        var host = await Build();

        var (committed, readBeforeCommitThrew, rootValue, childValue) =
            await (Task<(bool, bool, int, string)>)Helper("CommitWithValues", host, 120, 120)!;

        Assert.That(committed, Is.True);
        Assert.That(readBeforeCommitThrew, Is.True, "nothing has run before Commit - there's no value to read yet.");
        Assert.That(rootValue, Is.EqualTo(1200));
        Assert.That(childValue, Is.EqualTo("match-120"));
        Shutdown(host);
    }

    [Test]
    public async Task BodyValues_FromAChainThatFailed_NeverBecomeReadable() {
        // The root's body ran and produced a value, but its writes were reverted when the child failed - so that
        // value never describes anything real and must not be exposed.
        var host = await Build();

        var (committed, hasValue, readThrew) = await (Task<(bool, bool, bool)>)Helper("FailWithValue", host, 130)!;

        Assert.That(committed, Is.False);
        Assert.That(hasValue, Is.False);
        Assert.That(readThrew, Is.True);
        Shutdown(host);
    }

    [Test]
    public async Task AChildValue_FlowsIntoARootBody_InOneAtomicTransaction() {
        var host = await Build();
        Assert.That((await Call("InsertMatchDirectly", host, 140)).IsOk(), Is.True);

        var (committed, coins) = await (Task<(Result, int)>)Helper("SettleMatchIntoWallet", host, 140, 140)!;

        Assert.That(committed.IsOk(), Is.True);
        Assert.That(coins, Is.EqualTo(980));
        Assert.That(await Exists("WalletExists", host, 140), Is.True, "the Root body consumed the Child body's value - Child before Root, in Add order.");
        Shutdown(host);
    }

    [Test]
    public async Task BodiesRunInAddOrder_AcrossDatabases_ChildRootChild() {
        var host = await Build();

        var third = await (Task<int>)Helper("PingPong", host, 150, 150)!;

        Assert.That(third, Is.EqualTo(3), "child produced 1, root built 2 on it, child built 3 on that - each body saw the previous one's value.");
        Assert.That(await Exists("WalletExists", host, 150), Is.True);
        Assert.That(await Exists("MatchExists", host, 150), Is.True);
        Shutdown(host);
    }

    [Test]
    public async Task ReadingAValueWhoseBodyIsAddedLater_FailsWithItsOwnErrorKind_AndAppliesNothing() {
        var host = await Build();

        var result = await Call("ConsumeBeforeProduced", host, 160);

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetError().Kind, Is.EqualTo(ErrorKind.MultiTxValueNotProduced), "a clear, specific kind - not a generic SystemFailure.");
        Assert.That(await Exists("WalletExists", host, 160), Is.False);
        Shutdown(host);
    }

    [Test]
    public async Task AValueProducedBeforeALaterFailure_IsRevoked_AndItsBodysWritesReverted() {
        var host = await Build();
        Assert.That((await Call("InsertMatchDirectly", host, 170)).IsOk(), Is.True);

        var (committed, hasValue, scoreAfter) = await (Task<(bool, bool, int)>)Helper("ValueThenLaterFailure", host, 170)!;

        Assert.That(committed, Is.False);
        Assert.That(hasValue, Is.False, "the producing body ran, but the transaction failed - its value must not survive it.");
        Assert.That(scoreAfter, Is.EqualTo(0), "the child's staged update (score 99) was never applied.");
        Shutdown(host);
    }

    [Test]
    public async Task BodyValues_WithoutArgs_Work() {
        var host = await Build();

        Assert.That(await (Task<string>)Helper("RootOnlyValueWithoutArgs", host)!, Is.EqualTo("no-args"));
        Shutdown(host);
    }

    [Test]
    public async Task DisposingAChildNeverReactivatedAfterARestart_StillSettlesItsMultiTxPreparesBeforeDeleting() {
        var host = await Build();
        Assert.That((await Call("CommitWalletAndMatch", host, 180, 180)).IsOk(), Is.True);
        Shutdown(host);

        var restarted = await (Task<RhinoHost>)Helper("BuildRootOnly", dir)!;
        var disposed = await Call("DisposeChild", restarted);

        Assert.That(disposed.IsOk(), Is.True);
        Assert.That(Directory.Exists(Path.Combine(dir, "Children", "SessionDb", "s1")), Is.False);
        Shutdown(restarted);
        Assert.That(ChainLog.Open(dir).Unwrap().RetainedChainCount, Is.EqualTo(0),
            "the root consumed its prepare at restart, the inactive child's was settled before deletion - nothing left to keep.");
    }

    // ---- Locked (LockMultiTx) ----

    [Test]
    public async Task Locked_AChildValueComesStraightBack_AndARootStepUsesIt_AfterPlainCodeAndAnAwait() {
        var host = await Build();
        Assert.That((await Call("InsertMatchDirectly", host, 200)).IsOk(), Is.True);

        var (committed, score) = await (Task<(Result, int)>)Helper("LockedSettle", host, 200, 200)!;

        Assert.That(committed.IsOk(), Is.True);
        Assert.That(score, Is.EqualTo(600), "the child step's value was returned directly to the caller's code.");
        Assert.That(await Exists("WalletExists", host, 200), Is.True);
        Shutdown(host);
    }

    [Test]
    public async Task Locked_HoldsItsDatabasesUntilCommit_SoOtherRequestsWaitAndThenSeeTheCommittedState() {
        var host = await Build();

        var (finishedWhileHeld, sawTheRow) = await (Task<(bool, bool)>)Helper("HoldsTheRootUntilCommit", host, 210)!;

        Assert.That(finishedWhileHeld, Is.False, "an ordinary BeginTx on a held database waits for the locked transaction to end.");
        Assert.That(sawTheRow, Is.True, "and once released, it sees the committed row.");
        Shutdown(host);
    }

    [Test]
    public async Task Locked_AFailedStep_AbortsEverythingAtOnce_AndReleasesTheDatabases() {
        var host = await Build();

        var (step, commit) = await (Task<(Result, Result)>)Helper("LockedStepFailure", host, 220)!;

        Assert.That(step.GetError().Kind, Is.EqualTo(ErrorKind.Custom));
        Assert.That(commit.IsError(), Is.True, "Commit after a failed step returns that failure - nothing is applied.");
        var read = Exists("WalletExists", host, 220);
        Assert.That(await Task.WhenAny(read, Task.Delay(5000)), Is.SameAs(read), "the Root was released at the failure, not held until the caller gave up.");
        Assert.That(await read, Is.False, "the earlier Root step's staged insert never applied.");
        Shutdown(host);
    }

    [Test]
    public async Task Locked_ADatabaseNotDeclaredAtOpen_IsRejectedAsAnError() {
        var host = await Build();

        var result = await Call("LockedUndeclared", host);

        Assert.That(result.GetError().Kind, Is.EqualTo(ErrorKind.MultiTxParticipantNotDeclared));
        Shutdown(host);
    }

    [Test]
    public async Task OpenMultiTx_DeclaringNothing_IsAnError() {
        var host = await Build();

        var result = await Call("OpenMultiTxDeclaringNothing", host);

        Assert.That(result.GetError().Kind, Is.EqualTo(ErrorKind.MultiTxNoParticipantsDeclared));
        Shutdown(host);
    }

    [Test]
    public async Task AChildFromAHookCtx_ThroughResultReturningApis_IsAnErrorNotAThrow() {
        var host = await Build();

        var (beginTx, openMultiTx) = await (Task<(Result, Result)>)Helper("ChildFromAHookCtxThroughResultApis", host)!;

        Assert.That(beginTx.GetError().Kind, Is.EqualTo(ErrorKind.ChildDatabaseRequiresHost));
        Assert.That(openMultiTx.GetError().Kind, Is.EqualTo(ErrorKind.ChildDatabaseRequiresHost));
        Shutdown(host);
    }

    [Test]
    public async Task Locked_DisposedWithoutCommit_AppliesNothing_AndReleases() {
        var host = await Build();

        await (Task)Helper("LockedDisposeWithoutCommit", host, 230)!;

        Assert.That(await Exists("WalletExists", host, 230), Is.False);
        Shutdown(host);
    }

    [Test]
    public async Task Locked_IsSingleUse_ReportedAsAnError() {
        var host = await Build();

        var (run, commit) = await (Task<(Result, Result)>)Helper("LockedAfterCommit", host)!;

        Assert.That(run.GetError().Kind, Is.EqualTo(ErrorKind.MultiTxAlreadyFinished));
        Assert.That(commit.GetError().Kind, Is.EqualTo(ErrorKind.MultiTxAlreadyFinished));
        Shutdown(host);
    }

    [Test]
    public async Task Locked_ConcurrentTransactionsDeclaringInOppositeOrders_NeverDeadlock() {
        var host = await Build();

        var all = Task.WhenAll(Enumerable.Range(300, 30).Select(i => Call("LockedBothWays", host, i, i % 2 == 0)));
        var finished = await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(30)));

        Assert.That(finished, Is.SameAs(all), "holding always happens Root first, then Children - declaration order doesn't matter.");
        Assert.That(all.Result.All(r => r.IsOk()), Is.True);
        Shutdown(host);
    }

    [Test]
    public async Task Locked_CrashAfterEveryPrepareIsDurable_RestartCompletesTheCommit() {
        var host = await Build();
        await Call("LockedThenCrash", host, 240, 240, MultiTxCrashPoint.AfterAllDurablePrepares);
        Shutdown(host);

        var restarted = await Build();

        Assert.That(await Exists("WalletExists", restarted, 240), Is.True, "same commit path as the planned transaction - same roll-forward.");
        Assert.That(await Exists("MatchExists", restarted, 240), Is.True);
        Shutdown(restarted);
    }

    [Test]
    public async Task Locked_CrashBeforeEveryPrepareIsDurable_RestartAbortsBothShares() {
        var host = await Build();
        await Call("LockedThenCrash", host, 250, 250, MultiTxCrashPoint.AfterFirstDurablePrepare);
        Shutdown(host);

        var restarted = await Build();

        Assert.That(await Exists("WalletExists", restarted, 250), Is.False);
        Assert.That(await Exists("MatchExists", restarted, 250), Is.False);
        Shutdown(restarted);
    }

    // ---- Per-step apply ----

    [Test]
    public async Task Locked_ALaterRunSeesAnEarlierRunsInsert_OnTheSameDatabase() {
        var host = await Build();

        var (sawInsert, committed) = await (Task<(bool, Result)>)Helper("LockedReadsItsOwnInsert", host, 500)!;

        Assert.That(sawInsert, Is.True, "the first Run's insert is already in the table when the second Run reads.");
        Assert.That(committed.IsOk(), Is.True);
        Assert.That(await Exists("WalletExists", host, 500), Is.True);
        Shutdown(host);
    }

    [Test]
    public async Task Locked_ALaterRunSeesAnEarlierRunsUpdate_OnAChild() {
        var host = await Build();
        Assert.That((await Call("InsertMatchDirectly", host, 510)).IsOk(), Is.True);

        var (scoreSeen, committed) = await (Task<(int, Result)>)Helper("LockedReadsItsOwnUpdate", host, 510)!;

        Assert.That(scoreSeen, Is.EqualTo(50));
        Assert.That(committed.IsOk(), Is.True);
        Assert.That(await (Task<int>)Helper("MatchScore", host, 510)!, Is.EqualTo(50));
        Shutdown(host);
    }

    [Test]
    public async Task Locked_ALaterRunNoLongerFindsARowAnEarlierRunDeleted() {
        var host = await Build();
        Assert.That((await Call("InsertMatchDirectly", host, 520)).IsOk(), Is.True);

        var (seenAfterDelete, committed) = await (Task<(bool, Result)>)Helper("LockedReadsItsOwnDelete", host, 520)!;

        Assert.That(seenAfterDelete, Is.False);
        Assert.That(committed.IsOk(), Is.True);
        Assert.That(await Exists("MatchExists", host, 520), Is.False);
        Shutdown(host);
    }

    [Test]
    public async Task Locked_RollbackAfterSeveralAppliedRuns_RestoresBothDatabasesExactly() {
        // Undo has to span every Run, not just the last one: two inserts, a delete, and two updates of one row
        // (the second update's undo must restore the first's value, and the first's undo the original).
        var host = await Build();
        Assert.That((await Call("InsertMatchDirectly", host, 532)).IsOk(), Is.True);
        Assert.That((await Call("InsertMatchDirectly", host, 533)).IsOk(), Is.True);

        await (Task)Helper("LockedSeveralRunsThenRollback", host, 530, 531, 532, 533)!;

        Assert.That(await Exists("WalletExists", host, 530), Is.False, "an insert from the first Run is reverted too, not only the last Run's.");
        Assert.That(await Exists("WalletExists", host, 531), Is.False);
        Assert.That(await (Task<int>)Helper("MatchScore", host, 532)!, Is.EqualTo(0), "both updates undone, back to the original row.");
        Assert.That(await Exists("MatchExists", host, 533), Is.True, "the deleted row is back.");
        Assert.That(await (Task<int>)Helper("MatchCount", host)!, Is.EqualTo(2));
        Assert.That((await Call("InsertWalletDirectly", host, 530)).IsOk(), Is.True, "the reverted insert left no stale primary-index entry behind.");
        Shutdown(host);
    }

    [Test]
    public async Task Locked_AFailingRunAfterAnAppliedOne_RevertsTheApplied_AndDropsItsOwnStagedWrite() {
        var host = await Build();

        var (failed, commit) = await (Task<(Result, Result)>)Helper("LockedAppliedRunThenAFailingRunThatStagedAWrite", host, 540, 541)!;

        Assert.That(failed.GetError().Kind, Is.EqualTo(ErrorKind.Custom));
        Assert.That(commit.IsError(), Is.True);
        Assert.That(await Exists("WalletExists", host, 540), Is.False);
        Assert.That((await Call("InsertWalletDirectly", host, 542)).IsOk(), Is.True, "the next ordinary transaction on the Root.");
        Assert.That(await Exists("WalletExists", host, 541), Is.False,
            "the failed body's staged insert must have been discarded - not left in the shared transaction for the next apply to pick up.");
        Shutdown(host);
    }

    [Test]
    public async Task Locked_ADuplicateKeyAcrossRuns_FailsAtTheSecondRun_NotAtCommit() {
        var host = await Build();

        var (second, commit) = await (Task<(Result, Result)>)Helper("LockedDuplicateAcrossRuns", host, 550)!;

        Assert.That(second.IsError(), Is.True, "the first insert is already in the table, so the second Run's own apply rejects it.");
        Assert.That(second.GetError().Kind, Is.EqualTo(ErrorKind.DuplicateKey));
        Assert.That(commit.GetError().Kind, Is.EqualTo(ErrorKind.DuplicateKey), "Commit after a failed Run returns that failure.");
        Assert.That(await Exists("WalletExists", host, 550), Is.False);
        Shutdown(host);
    }

    [Test]
    public async Task Locked_SeveralRunsOnOneDatabase_ShareOneLsn_InTheRingAndInItsSingleWalEntry() {
        var host = await Build();

        Assert.That((await Call("LockedThreeRootRuns", host, 560, 561)).IsOk(), Is.True);
        var ringLsns = await (Task<ulong[]>)Helper("WalletRingLsns", host)!;
        Shutdown(host);

        Assert.That(ringLsns, Has.Length.EqualTo(3));
        Assert.That(ringLsns.Distinct().Count(), Is.EqualTo(1), "one transaction, one LSN - not one per Run.");
        var rootWal = WriteAheadLog.ReadEntriesShared(Path.Combine(dir, "wal.dat")).Unwrap();
        Assert.That(rootWal, Has.Length.EqualTo(1));
        Assert.That(rootWal[0].Kind, Is.EqualTo(WalEntryKind.Operation));
        Assert.That(rootWal[0].Changes, Has.Length.EqualTo(3), "every Run's changes collected into the one entry.");
        Assert.That(rootWal[0].Lsn, Is.EqualTo(ringLsns[0]), "a reconnect cursor taken from the ring must match the WAL.");
    }

    [Test]
    public async Task Locked_RollbackAfterSeveralRuns_RevertsTheirRingEntries() {
        var host = await Build();

        await (Task)Helper("LockedTwoRootRunsThenRollback", host, 570, 571)!;

        Assert.That(await (Task<ulong[]>)Helper("WalletRingLsns", host)!, Is.Empty, "a client must never be able to sync a change that was rolled back.");
        Shutdown(host);
    }

    [Test]
    public async Task Locked_ARequestQueuedBehindTheLock_NeverSeesAnAppliedButRolledBackRow() {
        var host = await Build();

        var (finishedWhileHeld, sawTheRow) = await (Task<(bool, bool)>)Helper("OtherRequestBehindARolledBackLock", host, 580)!;

        Assert.That(finishedWhileHeld, Is.False, "the row is in the table, but the lock keeps every other request out until the end.");
        Assert.That(sawTheRow, Is.False, "and by then it was reverted.");
        Shutdown(host);
    }

    [Test]
    public async Task Locked_SeveralRunsPerDatabase_CrashAfterEveryPrepareIsDurable_RestartCompletesAllOfThem() {
        var host = await Build();
        await Call("LockedSeveralRunsPerDatabaseThenCrash", host, 590, 591, 590, 591, MultiTxCrashPoint.AfterAllDurablePrepares);
        Shutdown(host);

        var restarted = await Build();

        Assert.That(await Exists("WalletExists", restarted, 590), Is.True);
        Assert.That(await Exists("WalletExists", restarted, 591), Is.True, "each participant's prepare holds every Run's changes, not just the last.");
        Assert.That(await Exists("MatchExists", restarted, 590), Is.True);
        Assert.That(await Exists("MatchExists", restarted, 591), Is.True);
        Shutdown(restarted);
    }

    [Test]
    public async Task Locked_SeveralRunsPerDatabase_CrashBeforeEveryPrepareIsDurable_RestartHasNoneOfThem() {
        var host = await Build();
        await Call("LockedSeveralRunsPerDatabaseThenCrash", host, 600, 601, 600, 601, MultiTxCrashPoint.AfterFirstDurablePrepare);
        Shutdown(host);

        var restarted = await Build();

        Assert.That(await Exists("WalletExists", restarted, 600), Is.False);
        Assert.That(await Exists("WalletExists", restarted, 601), Is.False);
        Assert.That(await Exists("MatchExists", restarted, 600), Is.False);
        Assert.That(await Exists("MatchExists", restarted, 601), Is.False);
        Shutdown(restarted);
    }

    [Test]
    public async Task Planned_ALaterBodySeesAnEarlierBodysWrite_OnTheSameDatabase() {
        // Both kinds share the participant, so per-step apply holds for planned bodies too.
        var host = await Build();

        var (committed, secondSawFirst) = await (Task<(Result, bool)>)Helper("PlannedLaterBodySeesAnEarlierBodysWrite", host, 610)!;

        Assert.That(committed.IsOk(), Is.True);
        Assert.That(secondSawFirst, Is.True);
        Shutdown(host);
    }

    [Test]
    public async Task ACommittedChain_SurvivesARestart() {
        var host = await Build();
        Assert.That((await Call("CommitWalletAndMatch", host, 30, 30)).IsOk(), Is.True);
        Shutdown(host);

        var restarted = await Build();

        Assert.That(await Exists("WalletExists", restarted, 30), Is.True);
        Assert.That(await Exists("MatchExists", restarted, 30), Is.True);
        Shutdown(restarted);
    }

    [Test]
    public async Task CrashAfterEveryPrepareIsDurable_RestartCompletesTheCommitInsteadOfRevertingIt() {
        var host = await Build();
        var crashed = await Call("CommitWalletAndMatchThenCrash", host, 40, 40, MultiTxCrashPoint.AfterAllDurablePrepares);
        Assert.That(crashed.IsError(), Is.True, "the simulated crash cut Commit short before it could answer.");
        Shutdown(host);

        var restarted = await Build();

        Assert.That(await Exists("WalletExists", restarted, 40), Is.True, "the root's share was on disk and so was the child's - completed, not reverted.");
        Assert.That(await Exists("MatchExists", restarted, 40), Is.True);
        Shutdown(restarted);
    }

    [Test]
    public async Task CrashBeforeEveryPrepareIsDurable_RestartAbortsBothShares() {
        var host = await Build();
        await Call("CommitWalletAndMatchThenCrash", host, 50, 50, MultiTxCrashPoint.AfterFirstDurablePrepare);
        Shutdown(host);

        var restarted = await Build();

        Assert.That(await Exists("WalletExists", restarted, 50), Is.False, "only the root's share reached disk - the child's died in memory, so completing is impossible.");
        Assert.That(await Exists("MatchExists", restarted, 50), Is.False);
        Shutdown(restarted);
    }

    [Test]
    public async Task ARestartAfterACrash_LeavesTheDatabasesFullyUsable() {
        var host = await Build();
        await Call("CommitWalletAndMatchThenCrash", host, 60, 60, MultiTxCrashPoint.AfterAllDurablePrepares);
        Shutdown(host);
        var restarted = await Build();

        var next = await Call("CommitWalletAndMatch", restarted, 61, 61);

        Assert.That(next.IsOk(), Is.True);
        Assert.That(await Exists("WalletExists", restarted, 61), Is.True);
        Assert.That(await Exists("MatchExists", restarted, 61), Is.True);
        Shutdown(restarted);
    }
}
