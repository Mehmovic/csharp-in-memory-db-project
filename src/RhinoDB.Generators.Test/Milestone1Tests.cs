using RhinoDB.Core;
using RhinoDB.Lib.Execution;

namespace RhinoDB.Test.Generators;

// Milestone 1 [CHECK IN]: the smallest possible vertical slice through the
// entire pipeline - one Instant-kind table, primary key only, no secondary
// indexes - proving attribute parsing, TableModel/DatabaseModel resolution,
// generated Ops/Transaction/DbContext-partial emission, and the new
// DbExecutionLoop tx-threading all work together end to end via a real
// ctx.Run call.
public class Milestone1Tests {
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
    public async Task Insert_ThenGet_ReturnsTheInsertedRow() {
        var (db, txType, asm) = NewDb();
        var widget = NewWidget(asm, 1, "Ada", 10);

        var insertResult = await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Widget.Insert((dynamic)widget); return Result.Ok(); }, PropagationMode.Optimistic);
        Assert.That(insertResult.IsOk(), Is.True);

        var getResult = await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                var found = (bool)GeneratorTestHost.InvokeHelper(asm, "TestNs.TestHelpers", "WidgetFindIsOk", (object)((dynamic)tx).Widget, 1)!;
                return found ? Result.Ok() : Result.Error(DbError.IndexKeyNotFound());
            }, PropagationMode.Optimistic);
        Assert.That(getResult.IsOk(), Is.True);
    }

    [Test]
    public async Task Update_ThenGet_ReturnsTheUpdatedRow() {
        var (db, txType, asm) = NewDb();
        var original = NewWidget(asm, 1, "Ada", 10);
        var updated = NewWidget(asm, 1, "Ada", 42);

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Widget.Insert((dynamic)original); return Result.Ok(); }, PropagationMode.Optimistic);
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Widget.Update(1, (dynamic)updated); return Result.Ok(); }, PropagationMode.Optimistic);

        var stock = -1;
        var getResult = await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                stock = (int)GeneratorTestHost.InvokeHelper(asm, "TestNs.TestHelpers", "WidgetStock", (object)((dynamic)tx).Widget, 1)!;
                return Result.Ok();
            }, PropagationMode.Optimistic);

        Assert.That(getResult.IsOk(), Is.True);
        Assert.That(stock, Is.EqualTo(42));
    }

    [Test]
    public async Task Delete_ThenGet_ReportsNotFound() {
        var (db, txType, asm) = NewDb();
        var widget = NewWidget(asm, 1, "Ada", 10);

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Widget.Insert((dynamic)widget); return Result.Ok(); }, PropagationMode.Optimistic);
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Widget.Delete(1); return Result.Ok(); }, PropagationMode.Optimistic);

        var foundAfterDelete = true;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                foundAfterDelete = (bool)GeneratorTestHost.InvokeHelper(asm, "TestNs.TestHelpers", "WidgetFindIsOk", (object)((dynamic)tx).Widget, 1)!;
                return Result.Ok();
            }, PropagationMode.Optimistic);

        Assert.That(foundAfterDelete, Is.False);
    }

    [Test]
    public async Task InsertThenGet_WithinTheSameOperation_DoesNotSeeTheStagedRow() {
        // Read-your-own-writes was deliberately removed: Find never scans
        // uncommitted changes, so a not-yet-applied Insert is invisible to a
        // Find in the same operation - it only becomes visible once Apply()
        // has actually run, from a later operation.
        var (db, txType, asm) = NewDb();
        var widget = NewWidget(asm, 1, "Ada", 10);

        var foundWithinSameOperation = true;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                dtx.Widget.Insert((dynamic)widget);
                foundWithinSameOperation = (bool)GeneratorTestHost.InvokeHelper(asm, "TestNs.TestHelpers", "WidgetFindIsOk", (object)dtx.Widget, 1)!;
                return Result.Ok();
            }, PropagationMode.Optimistic);

        Assert.That(foundWithinSameOperation, Is.False);
    }
}
