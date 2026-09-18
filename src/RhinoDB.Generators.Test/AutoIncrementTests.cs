using RhinoDB.Core;
using RhinoDB.Lib.Execution;

namespace RhinoDB.Test.Generators;

public class AutoIncrementTests {
    private const string Source = """
        using RhinoDB.Core.Tables;
        using RhinoDB.Lib.Execution;

        namespace TestNs;

        [Database]
        public partial class GadgetDb : DbContext<GadgetDbTransaction> { }

        [Table(TableKind.Instant, typeof(GadgetDb))]
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

        var insertResult = await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Gadget.Insert((dynamic)gadget); return Result.Ok(); },
            PropagationMode.Optimistic);

        var assignedId = -1;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                assignedId = dtx.Gadget.Find(1).IsOk() ? 1 : -1;
                return Result.Ok();
            }, PropagationMode.Optimistic);

        Assert.That(insertResult.IsOk(), Is.True);
        Assert.That(assignedId, Is.EqualTo(1));
    }

    [Test]
    public async Task TwoZeroIdInserts_InTheSameOperation_GetDistinctSequentialIds() {
        var (db, txType, asm) = NewDb();
        var first = NewGadget(asm, 0, "First");
        var second = NewGadget(asm, 0, "Second");

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                dtx.Gadget.Insert((dynamic)first);
                dtx.Gadget.Insert((dynamic)second);
                return Result.Ok();
            }, PropagationMode.Optimistic);

        var foundBoth = false;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                foundBoth = dtx.Gadget.Find(1).IsOk() && dtx.Gadget.Find(2).IsOk();
                return Result.Ok();
            }, PropagationMode.Optimistic);

        Assert.That(foundBoth, Is.True);
    }

    [Test]
    public async Task Insert_WithExplicitNonZeroId_UsesTheSuppliedValue() {
        var (db, txType, asm) = NewDb();
        var gadget = NewGadget(asm, 42, "Explicit");

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Gadget.Insert((dynamic)gadget); return Result.Ok(); },
            PropagationMode.Optimistic);

        var found = false;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { found = ((dynamic)tx).Gadget.Find(42).IsOk(); return Result.Ok(); },
            PropagationMode.Optimistic);

        Assert.That(found, Is.True);
    }

    [Test]
    public async Task CounterPersists_AcrossSeparateOperations() {
        var (db, txType, asm) = NewDb();

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Gadget.Insert((dynamic)NewGadget(asm, 0, "One")); return Result.Ok(); },
            PropagationMode.Optimistic);
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Gadget.Insert((dynamic)NewGadget(asm, 0, "Two")); return Result.Ok(); },
            PropagationMode.Optimistic);

        var foundSecond = false;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                foundSecond = dtx.Gadget.Find(2).IsOk();
                return Result.Ok();
            }, PropagationMode.Optimistic);

        Assert.That(foundSecond, Is.True);
    }

    // Proves genuine per-field independence, not just "both fields happen to
    // move together" - Id's counter is called on every insert, Sequence's
    // only when Sequence is left at 0. Row A supplies an explicit Sequence
    // (its counter never fires), row B leaves both at 0. If the two counters
    // were accidentally shared/aliased, Sequence would come back 2 on row B
    // (matching Id's second call) instead of the correct 1 (its own first call).
    [Test]
    public async Task TwoAutoIncrementFieldsOnOneTable_TrackIndependentSequences() {
        const string source = """
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class WidgetDb : DbContext<WidgetDbTransaction> { }

            [Table(TableKind.Instant, typeof(WidgetDb))]
            public readonly partial record struct Widget([PrimaryKey][AutoIncrement] int Id, [AutoIncrement] long Sequence, string Name);
            """;
        var (asm, _) = GeneratorTestHost.CompileAndLoad(source);
        var dbType = asm.GetType("TestNs.WidgetDb")!;
        var txType = asm.GetType("TestNs.WidgetDbTransaction")!;
        var widgetType = asm.GetType("TestNs.Widget")!;
        var db = Activator.CreateInstance(dbType)!;

        var rowA = Activator.CreateInstance(widgetType, 0, 999L, "A")!;
        var rowB = Activator.CreateInstance(widgetType, 0, 0L, "B")!;

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                dtx.Widget.Insert((dynamic)rowA);
                dtx.Widget.Insert((dynamic)rowB);
                return Result.Ok();
            }, PropagationMode.Optimistic);

        long sequenceOfB = -1;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                var found = dtx.Widget.Find(2);
                sequenceOfB = found.IsOk() ? (long)found.Unwrap().Sequence : -1;
                return Result.Ok();
            }, PropagationMode.Optimistic);

        Assert.That(sequenceOfB, Is.EqualTo(1));
    }

    [Test]
    public async Task AutoIncrementField_GuardedByAUniqueIndex_AutoAssignsDistinctValues() {
        const string source = """
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class TicketDb : DbContext<TicketDbTransaction> { }

            [Table(TableKind.Instant, typeof(TicketDb))]
            public readonly partial record struct Ticket([PrimaryKey] int Id, [Index(IndexKind.Hash, Uniqueness.Unique)][AutoIncrement] int Code, string Name);

            // QuerySingle is a ref struct and can never cross a dynamic call boundary (see
            // GeneratorTestHost.InvokeHelper) - this helper does the Idx.Code.Find(...) touching
            // as real static-typed C#, exposing only a reflection-safe (non-ref-struct) signature.
            public static class TestHelpers {
                public static bool CodeIsOk(TicketDbTicketOps ticket, int code) => ticket.Idx.Code.Find(code).Get().IsOk();
            }
            """;
        var (asm, _) = GeneratorTestHost.CompileAndLoad(source);
        var dbType = asm.GetType("TestNs.TicketDb")!;
        var txType = asm.GetType("TestNs.TicketDbTransaction")!;
        var ticketType = asm.GetType("TestNs.Ticket")!;
        var db = Activator.CreateInstance(dbType)!;

        var first = Activator.CreateInstance(ticketType, 1, 0, "First")!;
        var second = Activator.CreateInstance(ticketType, 2, 0, "Second")!;

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                dtx.Ticket.Insert((dynamic)first);
                dtx.Ticket.Insert((dynamic)second);
                return Result.Ok();
            }, PropagationMode.Optimistic);

        // Idx.Code.Find is index-backed only, never overlay-aware, so it only
        // sees rows after Apply() has physically run, i.e. from a later
        // operation, not the one that staged the insert.
        var foundBothByCode = false;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                var byCode1 = (bool)GeneratorTestHost.InvokeHelper(asm, "TestNs.TestHelpers", "CodeIsOk", ((dynamic)tx).Ticket, 1)!;
                var byCode2 = (bool)GeneratorTestHost.InvokeHelper(asm, "TestNs.TestHelpers", "CodeIsOk", ((dynamic)tx).Ticket, 2)!;
                foundBothByCode = byCode1 && byCode2;
                return Result.Ok();
            }, PropagationMode.Optimistic);

        Assert.That(foundBothByCode, Is.True);
    }

    [Test]
    public async Task SignedAutoIncrementField_WithNegativeExplicitValue_StillAutoAssigns() {
        const string source = """
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class NegDb : DbContext<NegDbTransaction> { }

            [Table(TableKind.Instant, typeof(NegDb))]
            public readonly partial record struct Item([PrimaryKey][AutoIncrement] int Id, string Name);
            """;
        var (asm, _) = GeneratorTestHost.CompileAndLoad(source);
        var dbType = asm.GetType("TestNs.NegDb")!;
        var txType = asm.GetType("TestNs.NegDbTransaction")!;
        var itemType = asm.GetType("TestNs.Item")!;
        var db = Activator.CreateInstance(dbType)!;

        var negative = Activator.CreateInstance(itemType, -5, "Negative")!;

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Item.Insert((dynamic)negative); return Result.Ok(); },
            PropagationMode.Optimistic);

        var assignedPositive = false;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { assignedPositive = ((dynamic)tx).Item.Find(1).IsOk(); return Result.Ok(); },
            PropagationMode.Optimistic);

        Assert.That(assignedPositive, Is.True);
    }

    [Test]
    public async Task UnsignedAutoIncrementField_TriggersOnlyOnExplicitZero() {
        const string source = """
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class CrateDb : DbContext<CrateDbTransaction> { }

            [Table(TableKind.Instant, typeof(CrateDb))]
            public readonly partial record struct Crate([PrimaryKey][AutoIncrement] uint Id, string Name);
            """;
        var (asm, _) = GeneratorTestHost.CompileAndLoad(source);
        var dbType = asm.GetType("TestNs.CrateDb")!;
        var txType = asm.GetType("TestNs.CrateDbTransaction")!;
        var crateType = asm.GetType("TestNs.Crate")!;
        var db = Activator.CreateInstance(dbType)!;

        var zero = Activator.CreateInstance(crateType, 0u, "Zero")!;
        var explicitValue = Activator.CreateInstance(crateType, 7u, "Seven")!;

        bool autoAssignedFound, explicitPreserved;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                dtx.Crate.Insert((dynamic)zero);
                dtx.Crate.Insert((dynamic)explicitValue);
                return Result.Ok();
            }, PropagationMode.Optimistic);
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                var byAuto = dtx.Crate.Find(1u);
                var byExplicit = dtx.Crate.Find(7u);
                autoAssignedFound = byAuto.IsOk();
                explicitPreserved = byExplicit.IsOk();
                Assert.That(autoAssignedFound, Is.True);
                Assert.That(explicitPreserved, Is.True);
                return Result.Ok();
            }, PropagationMode.Optimistic);
    }

    [Test]
    public void AutoIncrementOnNonIntegerField_ReportsRHINO007() {
        const string source = """
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class BadDb : DbContext<BadDbTransaction> { }

            [Table(TableKind.Instant, typeof(BadDb))]
            public readonly partial record struct Invoice([PrimaryKey] int Id, [AutoIncrement] decimal Amount);
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO007"));
    }
}
