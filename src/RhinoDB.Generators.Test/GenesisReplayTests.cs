using RhinoDB.Core;
using RhinoDB.Lib.Cold;
using RhinoDB.Lib.Execution;

namespace RhinoDB.Test.Generators;

// LoadFromGenesis reconstructs in-memory state purely from WAL history (archived + live tail),
// entirely bypassing libmdbx and the live WAL - see WalArchiveTests.cs for the underlying
// merge/gap/dedup logic; these tests are about the generated ReplayApply/ReplayChange dispatch
// and the two hard constraints: never touches cold storage or the live WAL, and can stop at an
// arbitrary point in history (the actual point of a "debug replay" tool, not just a slower way
// to reach today's state).
public class GenesisReplayTests {
    private const string Source = """
        using MemoryPack;
        using MessagePack;
        using RhinoDB.Core.Tables;
        using RhinoDB.Lib.Execution;

        namespace TestNs;

        [Database]
        public partial class GameDb : DbContext<GameDbTransaction> { }

        [Table(TableKind.Persistent, typeof(GameDb))]
        [MemoryPackable(GenerateType.VersionTolerant)]
        [MessagePackObject]
        public readonly partial record struct Club(
            [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
            [property: MemoryPackOrder(1)] [property: Key(1)] int Rating);
        """;

    static private object NewClub(System.Reflection.Assembly assembly, int id, int rating) {
        var t = assembly.GetType("TestNs.Club")!;
        return Activator.CreateInstance(t, id, rating)!;
    }

    static private Result InvokeLoadFromGenesis(Type loaderType, object db, object cold, long? upToLsn = null) {
        var loader = Activator.CreateInstance(loaderType)!;
        return (Result)loaderType.GetMethod("LoadFromGenesis")!.Invoke(loader, [db, cold, upToLsn, null])!;
    }

    [Test]
    public async Task LoadFromGenesis_ReconstructsTheSameFinalStateAsNormalLoad() {
        var dir = Path.Combine(Path.GetTempPath(), "rhinodb-genesis-replay-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try {
            var (asm, _) = GeneratorTestHost.CompileAndLoad(Source);
            var dbType = asm.GetType("TestNs.GameDb")!;
            var txType = asm.GetType("TestNs.GameDbTransaction")!;
            var loaderType = asm.GetType("TestNs.GameDbLoader")!;

            using (var cold = ColdStore.Open(dir).Unwrap()) {
                var db = Activator.CreateInstance(dbType, cold)!;

                await (Task<Result>)GeneratorTestHost.RunTransactional(
                    db, txType, (ctx, tx) => { ((dynamic)tx).Club.Insert((dynamic)NewClub(asm, 1, 80)); return Result.Ok(); },
                    PropagationMode.Confirmed);
                await (Task<Result>)GeneratorTestHost.RunTransactional(
                    db, txType, (ctx, tx) => { ((dynamic)tx).Club.Insert((dynamic)NewClub(asm, 2, 75)); return Result.Ok(); },
                    PropagationMode.Confirmed);
                await (Task<Result>)GeneratorTestHost.RunTransactional(
                    db, txType, (ctx, tx) => { ((dynamic)tx).Club.Update(1, (dynamic)NewClub(asm, 1, 90)); return Result.Ok(); },
                    PropagationMode.Confirmed);
            }

            int normalRating1, normalRating2;
            using (var normalCold = ColdStore.Open(dir).Unwrap()) {
                var normalDb = Activator.CreateInstance(dbType, normalCold)!;
                normalCold.CompleteRecovery();
                var normalLoader = Activator.CreateInstance(loaderType)!;
                await (Task)loaderType.GetMethod("LoadAsync")!.Invoke(normalLoader, [normalDb])!;

                var r1 = 0; var r2 = 0;
                await (Task<Result>)GeneratorTestHost.RunTransactional(
                    normalDb, txType, (ctx, tx) => {
                        dynamic dtx = tx;
                        r1 = (int)dtx.Club.Find(1).Unwrap().Rating;
                        r2 = (int)dtx.Club.Find(2).Unwrap().Rating;
                        return Result.Ok();
                    }, PropagationMode.Optimistic);
                normalRating1 = r1;
                normalRating2 = r2;
            }

            int replayRating1, replayRating2;
            using (var replayCold = ColdStore.Open(dir).Unwrap()) {
                var replayDb = Activator.CreateInstance(dbType, replayCold)!;
                var replayResult = InvokeLoadFromGenesis(loaderType, replayDb, replayCold);
                Assert.That(replayResult.IsOk(), Is.True);

                var r1 = 0; var r2 = 0;
                await (Task<Result>)GeneratorTestHost.RunTransactional(
                    replayDb, txType, (ctx, tx) => {
                        dynamic dtx = tx;
                        r1 = (int)dtx.Club.Find(1).Unwrap().Rating;
                        r2 = (int)dtx.Club.Find(2).Unwrap().Rating;
                        return Result.Ok();
                    }, PropagationMode.Optimistic);
                replayRating1 = r1;
                replayRating2 = r2;
            }

            Assert.That(replayRating1, Is.EqualTo(normalRating1).And.EqualTo(90), "Replay must apply the Update, not just the original Insert.");
            Assert.That(replayRating2, Is.EqualTo(normalRating2).And.EqualTo(75));
        } finally {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task LoadFromGenesis_WithUpToLsn_StopsAtAnEarlierIntermediateState() {
        var dir = Path.Combine(Path.GetTempPath(), "rhinodb-genesis-replay-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try {
            var (asm, _) = GeneratorTestHost.CompileAndLoad(Source);
            var dbType = asm.GetType("TestNs.GameDb")!;
            var txType = asm.GetType("TestNs.GameDbTransaction")!;
            var loaderType = asm.GetType("TestNs.GameDbLoader")!;

            using (var cold = ColdStore.Open(dir).Unwrap()) {
                var db = Activator.CreateInstance(dbType, cold)!;

                await (Task<Result>)GeneratorTestHost.RunTransactional(
                    db, txType, (ctx, tx) => { ((dynamic)tx).Club.Insert((dynamic)NewClub(asm, 1, 80)); return Result.Ok(); },
                    PropagationMode.Confirmed); // LSN 0 - a fresh store's nextLsn starts at -1, so the first commit is 0
                await (Task<Result>)GeneratorTestHost.RunTransactional(
                    db, txType, (ctx, tx) => { ((dynamic)tx).Club.Insert((dynamic)NewClub(asm, 2, 75)); return Result.Ok(); },
                    PropagationMode.Confirmed); // LSN 1
            }

            using var stoppedCold = ColdStore.Open(dir).Unwrap();
            var stoppedDb = Activator.CreateInstance(dbType, stoppedCold)!;
            var stoppedResult = InvokeLoadFromGenesis(loaderType, stoppedDb, stoppedCold, upToLsn: 0L);

            Assert.That(stoppedResult.IsOk(), Is.True);

            var club1Found = false;
            var club2Found = false;
            await (Task<Result>)GeneratorTestHost.RunTransactional(
                stoppedDb, txType, (ctx, tx) => {
                    dynamic dtx = tx;
                    club1Found = dtx.Club.Find(1).IsOk();
                    club2Found = dtx.Club.Find(2).IsOk();
                    return Result.Ok();
                }, PropagationMode.Optimistic);

            Assert.That(club1Found, Is.True, "LSN 0 (Club 1's insert) is within the stop point and must be applied.");
            Assert.That(club2Found, Is.False, "LSN 1 (Club 2's insert) is past upToLsn and must not be applied.");
        } finally {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task LoadFromGenesis_NeverWritesToTheLiveWalOrLibmdbx() {
        var dir = Path.Combine(Path.GetTempPath(), "rhinodb-genesis-replay-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try {
            var (asm, _) = GeneratorTestHost.CompileAndLoad(Source);
            var dbType = asm.GetType("TestNs.GameDb")!;
            var txType = asm.GetType("TestNs.GameDbTransaction")!;
            var loaderType = asm.GetType("TestNs.GameDbLoader")!;

            using (var cold = ColdStore.Open(dir).Unwrap()) {
                var db = Activator.CreateInstance(dbType, cold)!;
                await (Task<Result>)GeneratorTestHost.RunTransactional(
                    db, txType, (ctx, tx) => { ((dynamic)tx).Club.Insert((dynamic)NewClub(asm, 1, 80)); return Result.Ok(); },
                    PropagationMode.Confirmed);
            }

            var walPath = Path.Combine(dir, "wal.dat");
            var walLengthBefore = new FileInfo(walPath).Length;

            using (var replayCold = ColdStore.Open(dir).Unwrap()) {
                var replayDb = Activator.CreateInstance(dbType, replayCold)!;
                var replayResult = InvokeLoadFromGenesis(loaderType, replayDb, replayCold);
                Assert.That(replayResult.IsOk(), Is.True);
            }

            var walLengthAfter = new FileInfo(walPath).Length;
            Assert.That(walLengthAfter, Is.EqualTo(walLengthBefore), "Genesis replay must never append to the live WAL.");
        } finally {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
