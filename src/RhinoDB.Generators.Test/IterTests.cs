using RhinoDB.Core;
using RhinoDB.Lib.Cold;
using RhinoDB.Lib.Execution;

namespace RhinoDB.Generators.Test;

// Every generated Ops class (Instant and Persistent kind alike) exposes
// Iter(), a sequential enumeration of every row currently in memory in
// DenseArray offset order - real storage only, not overlay-aware (see the
// generator's own comment on Iter() for why).
public class IterTests {
    private const string InstantSource = """
        using MemoryPack;
        using MessagePack;
        using RhinoDB.Core.Tables;
        using RhinoDB.Lib.Execution;

        namespace TestNs;

        [Database]
        public partial class ShopDb : DbContext<ShopDbTransaction> { }

        [Table(TableKind.Instant, typeof(ShopDb))]
        [MemoryPackable(GenerateType.VersionTolerant)]
        [MessagePackObject]
        public readonly partial record struct Widget(
            [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
            [property: MemoryPackOrder(1)] [property: Key(1)] string Name);

        // QuerySet is a ref struct and can never cross a dynamic call boundary
        // (see GeneratorTestHost.InvokeHelper) - typed helpers instead.
        public static class TestHelpers {
            public static System.Collections.Generic.List<string> WidgetNames(ShopDbWidgetOps ops) {
                using var q = ops.Iter();
                var rows = q.Get().Unwrap();
                var names = new System.Collections.Generic.List<string>();
                for (var i = 0; i < rows.Length; i++) names.Add(rows[i].Name);
                return names;
            }
        }
        """;

    private const string PersistentSource = """
        using MemoryPack;
        using MessagePack;
        using RhinoDB.Core.Tables;
        using RhinoDB.Lib.Execution;

        namespace TestNs;

        [Database]
        public partial class VaultDb : DbContext<VaultDbTransaction> { }

        [Table(TableKind.Persistent, typeof(VaultDb))]
        [MemoryPackable(GenerateType.VersionTolerant)]
        [MessagePackObject]
        public readonly partial record struct Account(
            [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
            [property: MemoryPackOrder(1)] [property: Key(1)] decimal Balance);

        // QuerySet is a ref struct and can never cross a dynamic call boundary
        // (see GeneratorTestHost.InvokeHelper) - typed helpers instead.
        public static class TestHelpers {
            public static System.Collections.Generic.List<decimal> AccountBalances(VaultDbAccountOps ops) {
                using var q = ops.Iter();
                var rows = q.Get().Unwrap();
                var balances = new System.Collections.Generic.List<decimal>();
                for (var i = 0; i < rows.Length; i++) balances.Add(rows[i].Balance);
                return balances;
            }
        }
        """;

    static private object NewWidget(System.Reflection.Assembly assembly, int id, string name) {
        var t = assembly.GetType("TestNs.Widget")!;
        return Activator.CreateInstance(t, id, name)!;
    }

    static private object NewAccount(System.Reflection.Assembly assembly, int id, decimal balance) {
        var t = assembly.GetType("TestNs.Account")!;
        return Activator.CreateInstance(t, id, balance)!;
    }

    [Test]
    public async Task Instant_Iter_ReturnsEveryInsertedRow() {
        var (asm, _) = GeneratorTestHost.CompileAndLoad(InstantSource);
        var dbType = asm.GetType("TestNs.ShopDb")!;
        var txType = asm.GetType("TestNs.ShopDbTransaction")!;
        var db = Activator.CreateInstance(dbType)!;

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                dtx.Instant.Widget.Insert((dynamic)NewWidget(asm, 1, "A"));
                dtx.Instant.Widget.Insert((dynamic)NewWidget(asm, 2, "B"));
                dtx.Instant.Widget.Insert((dynamic)NewWidget(asm, 3, "C"));
                return Result.Ok();
            }, PropagationMode.Optimistic);

        var names = new List<string>();
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                names.AddRange((System.Collections.Generic.List<string>)GeneratorTestHost.InvokeHelper(asm, "TestNs.TestHelpers", "WidgetNames", (object)((dynamic)tx).Instant.Widget)!);
                return Result.Ok();
            }, PropagationMode.Optimistic);

        Assert.That(names, Is.EquivalentTo(new[] { "A", "B", "C" }));
    }

    [Test]
    public async Task Instant_Iter_AfterASwapRemovingDelete_StillEnumeratesTheRemainingRowsExactlyOnce() {
        var (asm, _) = GeneratorTestHost.CompileAndLoad(InstantSource);
        var dbType = asm.GetType("TestNs.ShopDb")!;
        var txType = asm.GetType("TestNs.ShopDbTransaction")!;
        var db = Activator.CreateInstance(dbType)!;

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                dtx.Instant.Widget.Insert((dynamic)NewWidget(asm, 1, "A"));
                dtx.Instant.Widget.Insert((dynamic)NewWidget(asm, 2, "B"));
                return Result.Ok();
            }, PropagationMode.Optimistic);
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Instant.Widget.Delete(1); return Result.Ok(); },
            PropagationMode.Optimistic);

        var names = new List<string>();
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                names.AddRange((System.Collections.Generic.List<string>)GeneratorTestHost.InvokeHelper(asm, "TestNs.TestHelpers", "WidgetNames", (object)((dynamic)tx).Instant.Widget)!);
                return Result.Ok();
            }, PropagationMode.Optimistic);

        Assert.That(names, Is.EquivalentTo(new[] { "B" }));
    }

    [Test]
    public async Task Persistent_Iter_ReturnsEveryInsertedRow() {
        var dir = Path.Combine(Path.GetTempPath(), "rhinodb-iter-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try {
            using var cold = ColdStore.Open(dir).Unwrap();
            var (asm, _) = GeneratorTestHost.CompileAndLoad(PersistentSource);
            var dbType = asm.GetType("TestNs.VaultDb")!;
            var txType = asm.GetType("TestNs.VaultDbTransaction")!;
            var db = Activator.CreateInstance(dbType, cold)!;

            await (Task<Result>)GeneratorTestHost.RunTransactional(
                db, txType, (ctx, tx) => {
                    dynamic dtx = tx;
                    dtx.Account.Insert((dynamic)NewAccount(asm, 1, 100m));
                    dtx.Account.Insert((dynamic)NewAccount(asm, 2, 200m));
                    return Result.Ok();
                }, PropagationMode.Optimistic);

            var balances = new List<decimal>();
            await (Task<Result>)GeneratorTestHost.RunTransactional(
                db, txType, (ctx, tx) => {
                    balances.AddRange((System.Collections.Generic.List<decimal>)GeneratorTestHost.InvokeHelper(asm, "TestNs.TestHelpers", "AccountBalances", (object)((dynamic)tx).Account)!);
                    return Result.Ok();
                }, PropagationMode.Optimistic);

            Assert.That(balances, Is.EquivalentTo(new[] { 100m, 200m }));
        } finally {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
