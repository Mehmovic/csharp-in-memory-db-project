using RhinoDB.Core;
using RhinoDB.Lib.Execution;

namespace RhinoDB.Test.Generators;

// Ports the staging-semantics edge cases ChangeSetTests.cs proved against the
// now-superseded hand-written ChangeSet<TKey,TRow> onto the real generated
// Ops class (WidgetOps), before ChangeSet itself gets deleted as dead code -
// same read-your-own-writes overlay, most-recent-staged-change-wins algorithm,
// just inlined per table by the generator instead of a shared generic type.
public class StagingSemanticsTests {
    private const string Source = """
        using RhinoDB.Core.Tables;
        using RhinoDB.Lib.Execution;

        namespace TestNs;

        [Database]
        public partial class WidgetDb : DbContext<WidgetDbTransaction> { }

        [Table(TableKind.Instant, typeof(WidgetDb))]
        public readonly partial record struct Widget([PrimaryKey] int Id, string Name, int Stock);
        """;

    static private (object Db, Type TxType, System.Reflection.Assembly Assembly) NewDb() {
        var (asm, _) = GeneratorTestHost.CompileAndLoad(Source);
        var dbType = asm.GetType("TestNs.WidgetDb")!;
        var txType = asm.GetType("TestNs.WidgetDbTransaction")!;
        var db = Activator.CreateInstance(dbType)!;
        return (db, txType, asm);
    }

    static private object NewWidget(System.Reflection.Assembly assembly, int id, string name, int stock) {
        var widgetType = assembly.GetType("TestNs.Widget")!;
        return Activator.CreateInstance(widgetType, id, name, stock)!;
    }

    [Test]
    public async Task InsertThenUpdate_SameKey_WithinOneOperation_MostRecentWins() {
        var (db, txType, asm) = NewDb();

        var stock = -1;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                dtx.Widget.Insert((dynamic)NewWidget(asm, 1, "Ada", 10));
                dtx.Widget.Update(1, (dynamic)NewWidget(asm, 1, "Ada", 42));
                stock = (int)dtx.Widget.Find(1).Unwrap().Stock;
                return Result.Ok();
            }, PropagationMode.Optimistic);

        Assert.That(stock, Is.EqualTo(42));
    }

    [Test]
    public async Task InsertThenDelete_SameKey_WithinOneOperation_ReportsNotFound() {
        var (db, txType, asm) = NewDb();

        var foundWithinSameOperation = true;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                dtx.Widget.Insert((dynamic)NewWidget(asm, 1, "Ada", 10));
                dtx.Widget.Delete(1);
                foundWithinSameOperation = dtx.Widget.Find(1).IsOk();
                return Result.Ok();
            }, PropagationMode.Optimistic);

        Assert.That(foundWithinSameOperation, Is.False);
    }

    [Test]
    public async Task DeleteThenInsert_SameKey_WithinOneOperation_UndoesTheDelete() {
        // A key deleted then reinserted within the same operation must read as
        // present again - the most-recent staged entry always wins, never a
        // "once deleted, always deleted for this operation" latch.
        var (db, txType, asm) = NewDb();

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Widget.Insert((dynamic)NewWidget(asm, 1, "Ada", 10)); return Result.Ok(); },
            PropagationMode.Optimistic);

        var stock = -1;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                dtx.Widget.Delete(1);
                dtx.Widget.Insert((dynamic)NewWidget(asm, 1, "Ada", 99));
                stock = (int)dtx.Widget.Find(1).Unwrap().Stock;
                return Result.Ok();
            }, PropagationMode.Optimistic);

        Assert.That(stock, Is.EqualTo(99));
    }

    [Test]
    public async Task DifferentKeys_AreTrackedIndependently_WithinOneOperation() {
        var (db, txType, asm) = NewDb();

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Widget.Insert((dynamic)NewWidget(asm, 2, "Bob", 5)); return Result.Ok(); },
            PropagationMode.Optimistic);

        var foundOne = false;
        var foundTwoAfterDelete = true;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                dtx.Widget.Insert((dynamic)NewWidget(asm, 1, "Ada", 10));
                dtx.Widget.Delete(2);
                foundOne = dtx.Widget.Find(1).IsOk();
                foundTwoAfterDelete = dtx.Widget.Find(2).IsOk();
                return Result.Ok();
            }, PropagationMode.Optimistic);

        Assert.That(foundOne, Is.True);
        Assert.That(foundTwoAfterDelete, Is.False);
    }
}
