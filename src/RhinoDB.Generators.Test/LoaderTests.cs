using RhinoDB.Core;
using RhinoDB.Lib.Cold;
using RhinoDB.Lib.Execution;

namespace RhinoDB.Test.Generators;

// {Db}Loader.LoadAsync eager-loads every non-Evictable Persistent-kind
// table's entire cold-storage contents at startup - the only way such a
// table could ever have data in memory, since it has no .Storage to load
// rows on demand. Evictable tables are left alone by default, but their
// BulkLoadFromCold() is still reachable (via reflection here, since it's
// internal - a real caller would be a LoadAsync override in the same
// generated assembly) for a consumer who wants to eager-load one anyway.
public class LoaderTests {
    private const string Source = """
        using RhinoDB.Core.Tables;
        using RhinoDB.Lib.Execution;

        namespace TestNs;

        [Database]
        public partial class GameDb : DbContext<GameDbTransaction> { }

        [Table(TableKind.Persistent, typeof(GameDb))]
        public readonly partial record struct Club([PrimaryKey] int Id, int Rating);

        [Table(TableKind.Persistent, typeof(GameDb), Evictable = true)]
        public readonly partial record struct Account([PrimaryKey] int Id, decimal Balance);

        [Table(TableKind.Instant, typeof(GameDb))]
        public readonly partial record struct Session([PrimaryKey] int Id);
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
                    club1Found = dtx.Club.Find(1).IsOk();
                    club2Found = dtx.Club.Find(2).IsOk();
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
                reopenedDb, txType, (ctx, tx) => { found = ((dynamic)tx).Account.Find(1).IsOk(); return Result.Ok(); },
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

            var createLoaderTx = dbType.GetMethod("CreateLoaderTransaction", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            dynamic tx = createLoaderTx.Invoke(reopenedDb, null)!;
            var accountsOps = tx.Account;
            var bulkLoad = ((object)accountsOps).GetType().GetMethod("BulkLoadFromCold", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            bulkLoad.Invoke(accountsOps, null);

            Assert.That((bool)accountsOps.Find(1).IsOk(), Is.True, "BulkLoadFromCold() must be usable directly by a custom LoadAsync override to eager-load an Evictable table too.");
        } finally {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public void InstantOnlyDatabase_DoesNotGenerateALoaderAtAll() {
        const string source = """
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class ShopDb : DbContext<ShopDbTransaction> { }

            [Table(TableKind.Instant, typeof(ShopDb))]
            public readonly partial record struct Widget([PrimaryKey] int Id);
            """;

        var (asm, _) = GeneratorTestHost.CompileAndLoad(source);

        Assert.That(asm.GetType("TestNs.ShopDbLoader"), Is.Null, "A database with no Persistent-kind tables has nothing to eager-load, so no loader should be generated.");
    }
}
