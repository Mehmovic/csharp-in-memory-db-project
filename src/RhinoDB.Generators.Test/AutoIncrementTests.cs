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

        long sequenceOfB = -1;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                dtx.Widgets.Insert((dynamic)rowA);
                dtx.Widgets.Insert((dynamic)rowB);
                dynamic found = dtx.Widgets.Get(2);
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
                dtx.Tickets.Insert((dynamic)first);
                dtx.Tickets.Insert((dynamic)second);
                return Result.Ok();
            }, PropagationMode.Optimistic);

        // Code() is a secondary-index accessor - not overlay-aware (that's
        // Milestone 4), so it only sees rows after Apply() has physically
        // run, i.e. from a later operation, not the one that staged the insert.
        var foundBothByCode = false;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                dynamic byCode1 = dtx.Tickets.Code(1);
                dynamic byCode2 = dtx.Tickets.Code(2);
                foundBothByCode = byCode1.IsOk() && byCode2.IsOk();
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

        var assignedPositive = false;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                dtx.Items.Insert((dynamic)negative);
                dynamic found = dtx.Items.Get(1);
                assignedPositive = found.IsOk();
                return Result.Ok();
            }, PropagationMode.Optimistic);

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
                dtx.Crates.Insert((dynamic)zero);
                dtx.Crates.Insert((dynamic)explicitValue);
                return Result.Ok();
            }, PropagationMode.Optimistic);
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                dynamic byAuto = dtx.Crates.Get(1u);
                dynamic byExplicit = dtx.Crates.Get(7u);
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
