using RhinoDB.Core;
using RhinoDB.Lib.Execution;

namespace RhinoDB.Generators.Test;

public class MultiDatabaseTableTests {
    private const string Source = """
        using MemoryPack;
        using MessagePack;
        using RhinoDB.Core.Tables;
        using RhinoDB.Lib.Execution;

        namespace TestNs;

        [Database]
        public partial class GameDb : DbContext<GameDbTransaction> { }

        [Database]
        public partial class ShardDb : DbContext<ShardDbTransaction> { }

        [Table(TableKind.Instant, typeof(GameDb))]
        [Table(TableKind.Instant, typeof(ShardDb))]
        [MemoryPackable(GenerateType.VersionTolerant)]
        [MessagePackObject]
        public readonly partial record struct Player(
            [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
            [property: MemoryPackOrder(1)] [property: Key(1)] string Name);
        """;

    [Test]
    public void SameRowType_GeneratesDistinctOpsClassesPerDatabase() {
        var (asm, _) = GeneratorTestHost.CompileAndLoad(Source);

        Assert.That(asm.GetType("TestNs.GameDbPlayerOps"), Is.Not.Null);
        Assert.That(asm.GetType("TestNs.ShardDbPlayerOps"), Is.Not.Null);
    }

    [Test]
    public async Task InsertingIntoOneDatabase_DoesNotAffectTheOther() {
        var (asm, _) = GeneratorTestHost.CompileAndLoad(Source);
        var gameDb = Activator.CreateInstance(asm.GetType("TestNs.GameDb")!)!;
        var shardDb = Activator.CreateInstance(asm.GetType("TestNs.ShardDb")!)!;
        var gameDbTxType = asm.GetType("TestNs.GameDbTransaction")!;
        var shardDbTxType = asm.GetType("TestNs.ShardDbTransaction")!;
        var player = Activator.CreateInstance(asm.GetType("TestNs.Player")!, 1, "Ada")!;

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            gameDb, gameDbTxType, (ctx, tx) => { ((dynamic)tx).Player.Insert((dynamic)player); return Result.Ok(); },
            PropagationMode.Optimistic);

        var foundInShard = false;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            shardDb, shardDbTxType, (ctx, tx) => { foundInShard = ((dynamic)tx).Player.Get(1).IsOk(); return Result.Ok(); },
            PropagationMode.Optimistic);

        Assert.That(foundInShard, Is.False);
    }

    [Test]
    public async Task InsertingTheSameKeyIntoBothDatabases_BothSucceedIndependently() {
        var (asm, _) = GeneratorTestHost.CompileAndLoad(Source);
        var gameDb = Activator.CreateInstance(asm.GetType("TestNs.GameDb")!)!;
        var shardDb = Activator.CreateInstance(asm.GetType("TestNs.ShardDb")!)!;
        var gameDbTxType = asm.GetType("TestNs.GameDbTransaction")!;
        var shardDbTxType = asm.GetType("TestNs.ShardDbTransaction")!;
        var player = Activator.CreateInstance(asm.GetType("TestNs.Player")!, 1, "Ada")!;

        var gameResult = await (Task<Result>)GeneratorTestHost.RunTransactional(
            gameDb, gameDbTxType, (ctx, tx) => { ((dynamic)tx).Player.Insert((dynamic)player); return Result.Ok(); },
            PropagationMode.Optimistic);
        var shardResult = await (Task<Result>)GeneratorTestHost.RunTransactional(
            shardDb, shardDbTxType, (ctx, tx) => { ((dynamic)tx).Player.Insert((dynamic)player); return Result.Ok(); },
            PropagationMode.Optimistic);

        Assert.That(gameResult.IsOk(), Is.True);
        Assert.That(shardResult.IsOk(), Is.True);
    }

    [Test]
    public async Task SameDatabase_TwoTablesOfTheSameRowType_DistinctAccessors_AreIndependent() {
        const string source = """
            using MemoryPack;
            using MessagePack;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class LeagueDb : DbContext<LeagueDbTransaction> { }

            [Table(TableKind.Instant, typeof(LeagueDb), Accessor = "PrimaryPlayers")]
            [Table(TableKind.Instant, typeof(LeagueDb), Accessor = "BackupPlayers")]
            [MemoryPackable(GenerateType.VersionTolerant)]
            [MessagePackObject]
            public readonly partial record struct Player(
                [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
                [property: MemoryPackOrder(1)] [property: Key(1)] string Name);
            """;

        var (asm, _) = GeneratorTestHost.CompileAndLoad(source);
        Assert.That(asm.GetType("TestNs.LeagueDbPrimaryPlayersOps"), Is.Not.Null);
        Assert.That(asm.GetType("TestNs.LeagueDbBackupPlayersOps"), Is.Not.Null);

        var db = Activator.CreateInstance(asm.GetType("TestNs.LeagueDb")!)!;
        var txType = asm.GetType("TestNs.LeagueDbTransaction")!;
        var player = Activator.CreateInstance(asm.GetType("TestNs.Player")!, 1, "Ada")!;

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).PrimaryPlayers.Insert((dynamic)player); return Result.Ok(); },
            PropagationMode.Optimistic);

        var foundInBackup = false;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { foundInBackup = ((dynamic)tx).BackupPlayers.Get(1).IsOk(); return Result.Ok(); },
            PropagationMode.Optimistic);

        Assert.That(foundInBackup, Is.False);
    }

    [Test]
    public void SameDatabase_TwoTablesWithTheSameAccessor_ReportsRHINO010() {
        const string source = """
            using MemoryPack;
            using MessagePack;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class DupAccessorDb : DbContext<DupAccessorDbTransaction> { }

            [Table(TableKind.Instant, typeof(DupAccessorDb), Accessor = "Players")]
            [Table(TableKind.Instant, typeof(DupAccessorDb), Accessor = "Players")]
            [MemoryPackable(GenerateType.VersionTolerant)]
            [MessagePackObject]
            public readonly partial record struct Player(
                [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
                [property: MemoryPackOrder(1)] [property: Key(1)] string Name);
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO010"));
    }
}
