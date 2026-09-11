using RhinoDB.Core;
using RhinoDB.Lib.Execution;

namespace RhinoDB.Test.Generators;

public class AutoIncrementTests {
    private const string Source = """
        using RhinoDB.Core.Tables;
        using RhinoDB.Lib.Execution;

        namespace TestNs;

        [Database]
        public partial class GadgetDb : DbContext { }

        [GenerateTable(TableKind.Instant, typeof(GadgetDb))]
        public readonly partial record struct Gadget([PrimaryKey][AutoIncrement] int Id, string Name);
        """;

    static private (object Db, Type TxType, System.Reflection.Assembly Assembly) NewDb() {
        var (asm, _) = GeneratorTestHost.CompileAndLoad(Source);
        var dbType = asm.GetType("TestNs.GadgetDb")!;
        var txType = asm.GetType("TestNs.GadgetDbTransaction")!;
        var db = Activator.CreateInstance(dbType)!;
        return (db, txType, asm);
    }

    static private object NewGadget(System.Reflection.Assembly assembly, int id, string name) {
        var gadgetType = assembly.GetType("TestNs.Gadget")!;
        return Activator.CreateInstance(gadgetType, id, name)!;
    }

    [Test]
    public async Task Insert_WithZeroId_AutoAssignsStartingAtOne() {
        var (db, txType, asm) = NewDb();
        var gadget = NewGadget(asm, 0, "Widget A");

        int assignedId = -1;
        var result = await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                dtx.Gadgets.Insert((dynamic)gadget);
                dynamic found = dtx.Gadgets.Get(1);
                assignedId = found.IsOk() ? 1 : -1;
                return Result.Ok();
            }, PropagationMode.Optimistic);

        Assert.That(result.IsOk(), Is.True);
        Assert.That(assignedId, Is.EqualTo(1));
    }

    [Test]
    public async Task TwoZeroIdInserts_InTheSameOperation_GetDistinctSequentialIds() {
        var (db, txType, asm) = NewDb();
        var first = NewGadget(asm, 0, "First");
        var second = NewGadget(asm, 0, "Second");

        var foundBoth = false;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                dtx.Gadgets.Insert((dynamic)first);
                dtx.Gadgets.Insert((dynamic)second);
                foundBoth = dtx.Gadgets.Get(1).IsOk() && dtx.Gadgets.Get(2).IsOk();
                return Result.Ok();
            }, PropagationMode.Optimistic);

        Assert.That(foundBoth, Is.True);
    }

    [Test]
    public async Task Insert_WithExplicitNonZeroId_UsesTheSuppliedValue() {
        var (db, txType, asm) = NewDb();
        var gadget = NewGadget(asm, 42, "Explicit");

        var found = false;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                dtx.Gadgets.Insert((dynamic)gadget);
                found = dtx.Gadgets.Get(42).IsOk();
                return Result.Ok();
            }, PropagationMode.Optimistic);

        Assert.That(found, Is.True);
    }

    [Test]
    public async Task CounterPersists_AcrossSeparateOperations() {
        var (db, txType, asm) = NewDb();

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Gadgets.Insert((dynamic)NewGadget(asm, 0, "One")); return Result.Ok(); },
            PropagationMode.Optimistic);
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Gadgets.Insert((dynamic)NewGadget(asm, 0, "Two")); return Result.Ok(); },
            PropagationMode.Optimistic);

        var foundSecond = false;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                foundSecond = dtx.Gadgets.Get(2).IsOk();
                return Result.Ok();
            }, PropagationMode.Optimistic);

        Assert.That(foundSecond, Is.True);
    }
}
