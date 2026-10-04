using RhinoDB.Core;
using RhinoDB.Lib.Execution;

namespace RhinoDB.Generators.Test;

// Ports the staging-semantics edge cases ChangeSetTests.cs proved against the
// now-superseded hand-written ChangeSet<TKey,TRow> onto the real generated
// Ops class (WidgetOps). The read-your-own-writes overlay these tests
// originally proved was deliberately removed (2026-09-18) - Find never scans
// uncommitted `changes`, so nothing staged in this operation is visible until
// Apply() actually runs, from a later operation. Tests below now prove that
// contract instead, checking real state in a follow-up operation.
public class StagingSemanticsTests {
    private const string Source = """
        using MemoryPack;
        using MessagePack;
        using RhinoDB.Core.Tables;
        using RhinoDB.Lib.Execution;

        namespace TestNs;

        [Database]
        public partial class WidgetDb : DbContext<WidgetDbTransaction> { }

        [Table(TableKind.Instant, typeof(WidgetDb))]
        [MemoryPackable(GenerateType.VersionTolerant)]
        [MessagePackObject]
        public readonly partial record struct Widget(
            [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
            [property: MemoryPackOrder(1)] [property: Key(1)] string Name,
            [property: MemoryPackOrder(2)] [property: Key(2)] int Stock);

        // QuerySet/QuerySingle are ref structs and can never cross a dynamic call
        // boundary (see GeneratorTestHost.InvokeHelper) - typed helpers instead.
        public static class TestHelpers {
            public static bool WidgetFindIsOk(WidgetDbWidgetOps ops, int id) => ops.Primary.Find(id).HasRow();
            public static int WidgetStock(WidgetDbWidgetOps ops, int id) => ops.Primary.Find(id).Get().Unwrap().Stock;
        }
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

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                dtx.Instant.Widget.Insert((dynamic)NewWidget(asm, 1, "Ada", 10));
                dtx.Instant.Widget.Update(1, (dynamic)NewWidget(asm, 1, "Ada", 42));
                return Result.Ok();
            }, PropagationMode.Optimistic);

        var stock = -1;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { stock = (int)GeneratorTestHost.InvokeHelper(asm, "TestNs.TestHelpers", "WidgetStock", (object)((dynamic)tx).Instant.Widget, 1)!; return Result.Ok(); },
            PropagationMode.Optimistic);

        Assert.That(stock, Is.EqualTo(42), "Most-recent-staged-change-wins applies at Apply() time, not via a same-operation overlay.");
    }

    [Test]
    public async Task InsertThenDelete_SameKey_WithinOneOperation_ReportsNotFound() {
        var (db, txType, asm) = NewDb();

        var foundWithinSameOperation = true;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                dtx.Instant.Widget.Insert((dynamic)NewWidget(asm, 1, "Ada", 10));
                dtx.Instant.Widget.Delete(1);
                foundWithinSameOperation = (bool)GeneratorTestHost.InvokeHelper(asm, "TestNs.TestHelpers", "WidgetFindIsOk", (object)dtx.Instant.Widget, 1)!;
                return Result.Ok();
            }, PropagationMode.Optimistic);

        Assert.That(foundWithinSameOperation, Is.False);
    }

    [Test]
    public async Task InsertThenDelete_SameKey_WithinOneOperation_TheRowStaysGoneAfterApply() {
        // The actual case the Delete/Apply restructuring (2026-09-18) exists to keep correct:
        // Delete's staging no longer captures a "before" snapshot via the (now-overlay-free)
        // primary-key accessor, and Apply()'s default case fetches oldRow fresh from storage
        // instead. Naively dropping the overlay without that restructuring would have left
        // Delete silently no-op (nothing to find yet - Insert hasn't been applied either), so
        // the row would stay permanently inserted. Verify it doesn't.
        var (db, txType, asm) = NewDb();

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                dtx.Instant.Widget.Insert((dynamic)NewWidget(asm, 1, "Ada", 10));
                dtx.Instant.Widget.Delete(1);
                return Result.Ok();
            }, PropagationMode.Optimistic);

        var foundAfterApply = true;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { foundAfterApply = (bool)GeneratorTestHost.InvokeHelper(asm, "TestNs.TestHelpers", "WidgetFindIsOk", (object)((dynamic)tx).Instant.Widget, 1)!; return Result.Ok(); },
            PropagationMode.Optimistic);

        Assert.That(foundAfterApply, Is.False, "Insert-then-Delete in one operation must not leave the row permanently inserted.");
    }

    [Test]
    public async Task DeleteThenInsert_SameKey_WithinOneOperation_UndoesTheDelete() {
        // A key deleted then reinserted within the same operation must read as
        // present again - the most-recent staged entry always wins, never a
        // "once deleted, always deleted for this operation" latch.
        var (db, txType, asm) = NewDb();

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Instant.Widget.Insert((dynamic)NewWidget(asm, 1, "Ada", 10)); return Result.Ok(); },
            PropagationMode.Optimistic);

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                dtx.Instant.Widget.Delete(1);
                dtx.Instant.Widget.Insert((dynamic)NewWidget(asm, 1, "Ada", 99));
                return Result.Ok();
            }, PropagationMode.Optimistic);

        var stock = -1;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { stock = (int)GeneratorTestHost.InvokeHelper(asm, "TestNs.TestHelpers", "WidgetStock", (object)((dynamic)tx).Instant.Widget, 1)!; return Result.Ok(); },
            PropagationMode.Optimistic);

        Assert.That(stock, Is.EqualTo(99), "Most-recent-staged-change-wins applies at Apply() time, not via a same-operation overlay.");
    }

    [Test]
    public async Task DifferentKeys_AreTrackedIndependently_WithinOneOperation() {
        var (db, txType, asm) = NewDb();

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Instant.Widget.Insert((dynamic)NewWidget(asm, 2, "Bob", 5)); return Result.Ok(); },
            PropagationMode.Optimistic);

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                dtx.Instant.Widget.Insert((dynamic)NewWidget(asm, 1, "Ada", 10));
                dtx.Instant.Widget.Delete(2);
                return Result.Ok();
            }, PropagationMode.Optimistic);

        var foundOne = false;
        var foundTwoAfterDelete = true;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                foundOne = (bool)GeneratorTestHost.InvokeHelper(asm, "TestNs.TestHelpers", "WidgetFindIsOk", (object)dtx.Instant.Widget, 1)!;
                foundTwoAfterDelete = (bool)GeneratorTestHost.InvokeHelper(asm, "TestNs.TestHelpers", "WidgetFindIsOk", (object)dtx.Instant.Widget, 2)!;
                return Result.Ok();
            }, PropagationMode.Optimistic);

        Assert.That(foundOne, Is.True);
        Assert.That(foundTwoAfterDelete, Is.False);
    }
}
