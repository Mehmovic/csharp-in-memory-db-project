using RhinoDB.Core;
using RhinoDB.Lib.Cold;
using RhinoDB.Lib.Execution;

namespace RhinoDB.Generators.Test;

// {Db}Loader.LoadAsync eager-loads every non-Evictable Persistent-kind
// table's entire cold-storage contents at startup - the only way such a
// table could ever have data in memory, since it has no .Storage to load
// rows on demand. Evictable tables are left alone by default, but their
// BulkLoadFromCold() is still reachable (via reflection here, since it's
// internal - a real caller would be a LoadAsync override in the same
// generated assembly) for a consumer who wants to eager-load one anyway.
public class LoaderTests {
    private const string Source = """
        using MemoryPack;
        using MessagePack;
        using RhinoDB.Core.Tables;
        using RhinoDB.Lib.Execution;

        namespace TestNs;

        [Database]
        public partial class GameDb : DbContext<GameDbTransaction> { }

        [Table<GameDb>(TableKind.Persistent)]
        [MemoryPackable(GenerateType.VersionTolerant)]
        [MessagePackObject]
        public readonly partial record struct Club(
            [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
            [property: MemoryPackOrder(1)] [property: Key(1)] int Rating);

        [Table<GameDb>(TableKind.Persistent, Evictable = true)]
        [MemoryPackable(GenerateType.VersionTolerant)]
        [MessagePackObject]
        public readonly partial record struct Account(
            [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
            [property: MemoryPackOrder(1)] [property: Key(1)] decimal Balance);

        [Table<GameDb>(TableKind.Instant)]
        [MemoryPackable(GenerateType.VersionTolerant)]
        [MessagePackObject]
        public readonly partial record struct Session([PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id);

        // QuerySet/QuerySingle are ref structs and can never cross a dynamic call
        // boundary (see GeneratorTestHost.InvokeHelper) - typed helpers instead.
        public static class TestHelpers {
            public static bool ClubFindIsOk(GameDbClubOps ops, int id) => ops.Primary.Find(id).HasRow();
            public static bool AccountFindIsOk(GameDbAccountOps ops, int id) => ops.Primary.Find(id).HasRow();
        }
        """;

    static private object NewClub(System.Reflection.Assembly assembly, int id, int rating) {
        var t = assembly.GetType("TestNs.Club")!;
        return Activator.CreateInstance(t, id, rating)!;
    }

    static private object NewAccount(System.Reflection.Assembly assembly, int id, decimal balance) {
        var t = assembly.GetType("TestNs.Account")!;
        return Activator.CreateInstance(t, id, balance)!;
    }

    [Test]
    public async Task LoadAsync_EagerLoadsEveryRowOfANonEvictableTable() {
        var dir = Path.Combine(Path.GetTempPath(), "rhinodb-loader-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try {
            System.Reflection.Assembly asm;
            Type dbType, txType, loaderType;
            using (var cold = ColdStore.Open(dir).Unwrap()) {
                (asm, _) = GeneratorTestHost.CompileAndLoad(Source);
                dbType = asm.GetType("TestNs.GameDb")!;
                txType = asm.GetType("TestNs.GameDbTransaction")!;
                loaderType = asm.GetType("TestNs.GameDbLoader")!;
                var db = Activator.CreateInstance(dbType, cold)!;

                var insert = await (Task<Result>)GeneratorTestHost.RunTransactional(
                    db, txType, (ctx, tx) => {
                        dynamic dtx = tx;
                        dtx.Club.Insert((dynamic)NewClub(asm, 1, 80));
                        dtx.Club.Insert((dynamic)NewClub(asm, 2, 75));
                        return Result.Ok();
                    }, PropagationMode.Confirmed);
                Assert.That(insert.IsOk(), Is.True);
            }

            using var reopenedCold = ColdStore.Open(dir).Unwrap();
            var reopenedDb = Activator.CreateInstance(dbType, reopenedCold)!;
            reopenedCold.CompleteRecovery();
            var loader = Activator.CreateInstance(loaderType)!;

            await (Task)loaderType.GetMethod("LoadAsync")!.Invoke(loader, [reopenedDb])!;

            bool club1Found = false, club2Found = false;
            await (Task<Result>)GeneratorTestHost.RunTransactional(
                reopenedDb, txType, (ctx, tx) => {
                    dynamic dtx = tx;
                    club1Found = (bool)GeneratorTestHost.InvokeHelper(asm, "TestNs.TestHelpers", "ClubFindIsOk", (object)dtx.Club, 1)!;
                    club2Found = (bool)GeneratorTestHost.InvokeHelper(asm, "TestNs.TestHelpers", "ClubFindIsOk", (object)dtx.Club, 2)!;
                    return Result.Ok();
                }, PropagationMode.Optimistic);

            Assert.That(club1Found, Is.True);
            Assert.That(club2Found, Is.True);
        } finally {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task LoadAsync_DoesNotEagerLoadAnEvictableTableByDefault() {
        var dir = Path.Combine(Path.GetTempPath(), "rhinodb-loader-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try {
            System.Reflection.Assembly asm;
            Type dbType, txType, loaderType;
            using (var cold = ColdStore.Open(dir).Unwrap()) {
                (asm, _) = GeneratorTestHost.CompileAndLoad(Source);
                dbType = asm.GetType("TestNs.GameDb")!;
                txType = asm.GetType("TestNs.GameDbTransaction")!;
                loaderType = asm.GetType("TestNs.GameDbLoader")!;
                var db = Activator.CreateInstance(dbType, cold)!;

                await (Task<Result>)GeneratorTestHost.RunTransactional(
                    db, txType, (ctx, tx) => { ((dynamic)tx).Account.Insert((dynamic)NewAccount(asm, 1, 100m)); return Result.Ok(); },
                    PropagationMode.Confirmed);
            }

            using var reopenedCold = ColdStore.Open(dir).Unwrap();
            var reopenedDb = Activator.CreateInstance(dbType, reopenedCold)!;
            var loader = Activator.CreateInstance(loaderType)!;

            await (Task)loaderType.GetMethod("LoadAsync")!.Invoke(loader, [reopenedDb])!;

            var found = false;
            await (Task<Result>)GeneratorTestHost.RunTransactional(
                reopenedDb, txType, (ctx, tx) => { found = (bool)GeneratorTestHost.InvokeHelper(asm, "TestNs.TestHelpers", "AccountFindIsOk", (object)((dynamic)tx).Account, 1)!; return Result.Ok(); },
                PropagationMode.Optimistic);

            Assert.That(found, Is.False, "An Evictable table must not be eager-loaded by the default loader.");
        } finally {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task LoadAsync_CanBeOverriddenToAlsoEagerLoadAnEvictableTable() {
        var dir = Path.Combine(Path.GetTempPath(), "rhinodb-loader-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try {
            System.Reflection.Assembly asm;
            Type dbType, txType, loaderType;
            using (var cold = ColdStore.Open(dir).Unwrap()) {
                (asm, _) = GeneratorTestHost.CompileAndLoad(Source);
                dbType = asm.GetType("TestNs.GameDb")!;
                txType = asm.GetType("TestNs.GameDbTransaction")!;
                loaderType = asm.GetType("TestNs.GameDbLoader")!;
                var db = Activator.CreateInstance(dbType, cold)!;

                await (Task<Result>)GeneratorTestHost.RunTransactional(
                    db, txType, (ctx, tx) => { ((dynamic)tx).Account.Insert((dynamic)NewAccount(asm, 1, 100m)); return Result.Ok(); },
                    PropagationMode.Confirmed);
            }

            using var reopenedCold = ColdStore.Open(dir).Unwrap();
            var reopenedDb = Activator.CreateInstance(dbType, reopenedCold)!;
            reopenedCold.CompleteRecovery();

            // Simulate a LoadAsync override without a real C#-authored
            // subclass: call the base default (Clubs), then reach the
            // Evictable table's internal BulkLoadFromCold() directly via
            // the same transaction a real override would use.
            var loader = Activator.CreateInstance(loaderType)!;
            await (Task)loaderType.GetMethod("LoadAsync")!.Invoke(loader, [reopenedDb])!;

            var createLoaderTx = dbType.GetMethod("CreateLoaderTransaction", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            dynamic tx = createLoaderTx.Invoke(reopenedDb, null)!;
            var accountsOps = tx.Account;
            var bulkLoad = ((object)accountsOps).GetType().GetMethod("BulkLoadFromCold", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            bulkLoad.Invoke(accountsOps, null);

            Assert.That((bool)GeneratorTestHost.InvokeHelper(asm, "TestNs.TestHelpers", "AccountFindIsOk", (object)accountsOps, 1)!, Is.True, "BulkLoadFromCold() must be usable directly by a custom LoadAsync override to eager-load an Evictable table too.");
        } finally {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task LoadAsync_ReSeedsAutoIncrementCounterFromLoadedRows_SoARestartDoesNotReuseIds() {
        const string source = """
            using MemoryPack;
            using MessagePack;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class LeagueDb : DbContext<LeagueDbTransaction> { }

            [Table<LeagueDb>(TableKind.Persistent)]
            [MemoryPackable(GenerateType.VersionTolerant)]
            [MessagePackObject]
            public readonly partial record struct Club(
                [PrimaryKey][AutoIncrement] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
                [property: MemoryPackOrder(1)] [property: Key(1)] int Rating);

            // QuerySingle is a ref struct and can never cross a dynamic call
            // boundary (see GeneratorTestHost.InvokeHelper) - typed helper instead.
            public static class TestHelpers {
                public static bool ClubFindIsOk(LeagueDbClubOps ops, int id) => ops.Primary.Find(id).HasRow();
            }
            """;

        var dir = Path.Combine(Path.GetTempPath(), "rhinodb-loader-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try {
            System.Reflection.Assembly asm;
            Type dbType, txType, loaderType, clubType;
            using (var cold = ColdStore.Open(dir).Unwrap()) {
                (asm, _) = GeneratorTestHost.CompileAndLoad(source);
                dbType = asm.GetType("TestNs.LeagueDb")!;
                txType = asm.GetType("TestNs.LeagueDbTransaction")!;
                loaderType = asm.GetType("TestNs.LeagueDbLoader")!;
                clubType = asm.GetType("TestNs.Club")!;
                var db = Activator.CreateInstance(dbType, cold)!;

                var insert = await (Task<Result>)GeneratorTestHost.RunTransactional(
                    db, txType, (ctx, tx) => {
                        dynamic dtx = tx;
                        dtx.Club.Insert((dynamic)Activator.CreateInstance(clubType, 0, 80)!);
                        dtx.Club.Insert((dynamic)Activator.CreateInstance(clubType, 0, 75)!);
                        return Result.Ok();
                    }, PropagationMode.Confirmed);
                Assert.That(insert.IsOk(), Is.True);
            }

            using var reopenedCold = ColdStore.Open(dir).Unwrap();
            var reopenedDb = Activator.CreateInstance(dbType, reopenedCold)!;
            reopenedCold.CompleteRecovery();
            var loader = Activator.CreateInstance(loaderType)!;
            await (Task)loaderType.GetMethod("LoadAsync")!.Invoke(loader, [reopenedDb])!;

            await (Task<Result>)GeneratorTestHost.RunTransactional(
                reopenedDb, txType, (ctx, tx) => { ((dynamic)tx).Club.Insert((dynamic)Activator.CreateInstance(clubType, 0, 60)!); return Result.Ok(); },
                PropagationMode.Optimistic);

            var thirdClubFound = false;
            await (Task<Result>)GeneratorTestHost.RunTransactional(
                reopenedDb, txType, (ctx, tx) => { thirdClubFound = (bool)GeneratorTestHost.InvokeHelper(asm, "TestNs.TestHelpers", "ClubFindIsOk", (object)((dynamic)tx).Club, 3)!; return Result.Ok(); },
                PropagationMode.Optimistic);

            Assert.That(thirdClubFound, Is.True,
                "After eager-loading 2 rows (ids 1-2), a new AutoIncrement insert must continue at id 3, not restart at id 1 and collide.");
        } finally {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public void InstantOnlyDatabase_DoesNotGenerateALoaderAtAll() {
        const string source = """
            using MemoryPack;
            using MessagePack;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class ShopDb : DbContext<ShopDbTransaction> { }

            [Table<ShopDb>(TableKind.Instant)]
            [MemoryPackable(GenerateType.VersionTolerant)]
            [MessagePackObject]
            public readonly partial record struct Widget([PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id);
            """;

        var (asm, _) = GeneratorTestHost.CompileAndLoad(source);

        Assert.That(asm.GetType("TestNs.ShopDbLoader"), Is.Null, "A database with no Persistent-kind tables has nothing to eager-load, so no loader should be generated.");
    }
}
