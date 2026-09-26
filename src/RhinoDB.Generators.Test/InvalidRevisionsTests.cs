namespace RhinoDB.Generators.Test;

// [InvalidRevisions(Revisions=)] - the row-type-scoped sibling of [Database(InvalidGenerations=)] (Part F,
// Phase 5). Persistent-kind only, since Instant tables have no historical revisions to invalidate at all -
// no [Migration] chain exists for them either, same precedent.
public class InvalidRevisionsTests {
    [Test]
    public void InvalidRevisionsContainsZero_ReportsRHINO027() {
        const string source = """
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class VaultDb : DbContext<VaultDbTransaction> { }

            [InvalidRevisions(Revisions = new[] { 0 })]
            [Table(TableKind.Persistent, typeof(VaultDb))]
            public readonly partial record struct Widget([PrimaryKey] int Id);
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO027"));
    }

    [Test]
    public void InvalidRevisionsContainsANegativeNumber_ReportsRHINO027() {
        const string source = """
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class VaultDb : DbContext<VaultDbTransaction> { }

            [InvalidRevisions(Revisions = new[] { -1 })]
            [Table(TableKind.Persistent, typeof(VaultDb))]
            public readonly partial record struct Widget([PrimaryKey] int Id);
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO027"));
    }

    [Test]
    public void NoInvalidRevisionsDeclared_DoesNotThrow_IsRevisionInvalidAlwaysFalse() {
        const string source = """
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class VaultDb : DbContext<VaultDbTransaction> { }

            [Table(TableKind.Persistent, typeof(VaultDb))]
            public readonly partial record struct Widget([PrimaryKey] int Id);
            """;

        var (asm, _) = GeneratorTestHost.CompileAndLoad(source);
        var opsType = asm.GetType("TestNs.VaultDbWidgetOps")!;
        var isInvalid = opsType.GetMethod("IsRevisionInvalid")!;

        Assert.That(isInvalid.Invoke(null, [0]), Is.False);
        Assert.That(isInvalid.Invoke(null, [7]), Is.False);
    }

    [Test]
    public void PositiveInvalidRevisionsDeclared_IsRevisionInvalidReflectsThemOnly() {
        const string source = """
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class VaultDb : DbContext<VaultDbTransaction> { }

            [InvalidRevisions(Revisions = new[] { 3, 5 })]
            [Table(TableKind.Persistent, typeof(VaultDb))]
            public readonly partial record struct Widget([PrimaryKey] int Id);
            """;

        var (asm, _) = GeneratorTestHost.CompileAndLoad(source);
        var opsType = asm.GetType("TestNs.VaultDbWidgetOps")!;
        var isInvalid = opsType.GetMethod("IsRevisionInvalid")!;

        Assert.That(isInvalid.Invoke(null, [3]), Is.True);
        Assert.That(isInvalid.Invoke(null, [5]), Is.True);
        Assert.That(isInvalid.Invoke(null, [1]), Is.False);
        Assert.That(isInvalid.Invoke(null, [4]), Is.False);
    }

    [Test]
    public void RowTypeBackingTwoTables_EachGetsItsOwnIndependentlyCorrectIsRevisionInvalid() {
        const string source = """
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class VaultDb : DbContext<VaultDbTransaction> { }

            [Database]
            public partial class ArchiveDb : DbContext<ArchiveDbTransaction> { }

            [InvalidRevisions(Revisions = new[] { 3 })]
            [Table(TableKind.Persistent, typeof(VaultDb))]
            [Table(TableKind.Persistent, typeof(ArchiveDb))]
            public readonly partial record struct Widget([PrimaryKey] int Id);
            """;

        var (asm, _) = GeneratorTestHost.CompileAndLoad(source);
        var vaultOpsType = asm.GetType("TestNs.VaultDbWidgetOps")!;
        var archiveOpsType = asm.GetType("TestNs.ArchiveDbWidgetOps")!;

        Assert.That(vaultOpsType.GetMethod("IsRevisionInvalid")!.Invoke(null, [3]), Is.True);
        Assert.That(archiveOpsType.GetMethod("IsRevisionInvalid")!.Invoke(null, [3]), Is.True);
        Assert.That(vaultOpsType.GetMethod("IsRevisionInvalid")!.Invoke(null, [1]), Is.False);
        Assert.That(archiveOpsType.GetMethod("IsRevisionInvalid")!.Invoke(null, [1]), Is.False);
    }
}
