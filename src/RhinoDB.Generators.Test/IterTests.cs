using RhinoDB.Core;
using RhinoDB.Lib.Cold;
using RhinoDB.Lib.Execution;

namespace RhinoDB.Test.Generators;

// Every generated Ops class (Instant and Persistent kind alike) exposes
// Iter(), a sequential enumeration of every row currently in memory in
// DenseArray offset order - real storage only, not overlay-aware (see the
// generator's own comment on Iter() for why).
public class IterTests {
    private const string InstantSource = """
        using RhinoDB.Core.Tables;
        using RhinoDB.Lib.Execution;

        namespace TestNs;

        [Database]
        public partial class ShopDb : DbContext<ShopDbTransaction> { }

        [Table(TableKind.Instant, typeof(ShopDb))]
        public readonly partial record struct Widget([PrimaryKey] int Id, string Name);
        """;

    private const string PersistentSource = """
        using RhinoDB.Core.Tables;
        using RhinoDB.Lib.Execution;

        namespace TestNs;

        [Database]
        public partial class VaultDb : DbContext<VaultDbTransaction> { }

        [Table(TableKind.Persistent, typeof(VaultDb))]
        public readonly partial record struct Account([PrimaryKey] int Id, decimal Balance);
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
                dtx.Widget.Insert((dynamic)NewWidget(asm, 1, "A"));
                dtx.Widget.Insert((dynamic)NewWidget(asm, 2, "B"));
                dtx.Widget.Insert((dynamic)NewWidget(asm, 3, "C"));
                return Result.Ok();
            }, PropagationMode.Optimistic);

        var names = new List<string>();
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                foreach (var widget in ((dynamic)tx).Widget.Iter()) names.Add((string)widget.Name);
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
                dtx.Widget.Insert((dynamic)NewWidget(asm, 1, "A"));
                dtx.Widget.Insert((dynamic)NewWidget(asm, 2, "B"));
                return Result.Ok();
            }, PropagationMode.Optimistic);
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Widget.Delete(1); return Result.Ok(); },
            PropagationMode.Optimistic);

        var names = new List<string>();
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                foreach (var widget in ((dynamic)tx).Widget.Iter()) names.Add((string)widget.Name);
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
                    foreach (var account in ((dynamic)tx).Account.Iter()) balances.Add((decimal)account.Balance);
                    return Result.Ok();
                }, PropagationMode.Optimistic);

            Assert.That(balances, Is.EquivalentTo(new[] { 100m, 200m }));
        } finally {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
