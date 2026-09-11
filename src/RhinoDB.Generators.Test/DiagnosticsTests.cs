namespace RhinoDB.Test.Generators;

// Every rule TableGenerator relies on is a diagnostic, not a silent
// assumption or a generator crash - see the file-level comment on
// TableGenerator.cs. GeneratorTestHost.CompileAndLoad already throws
// InvalidOperationException listing any generator-reported errors before
// even attempting to emit, so asserting on that exception's message is
// enough to prove a given rule violation is actually caught at compile time.
public class DiagnosticsTests {
    [Test]
    public void MissingPrimaryKey_ReportsRHINO001() {
        const string source = """
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class NoKeyDb : DbContext<NoKeyDbTransaction> { }

            [Table(TableKind.Instant, typeof(NoKeyDb))]
            public readonly partial record struct Widget(int Id, string Name);
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO001"));
    }

    [Test]
    public void EmptyIndexAccessor_ReportsRHINO002() {
        const string source = """
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class EmptyAccessorDb : DbContext<EmptyAccessorDbTransaction> { }

            [Table(TableKind.Instant, typeof(EmptyAccessorDb))]
            public readonly partial record struct Widget(
                [PrimaryKey] int Id,
                [Index(IndexKind.Hash, Uniqueness.Unique, Accessor = "")] string Sku);
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO002"));
    }

    [Test]
    public void EmptyPrimaryKeyAccessor_ReportsRHINO002() {
        const string source = """
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class EmptyPkAccessorDb : DbContext<EmptyPkAccessorDbTransaction> { }

            [Table(TableKind.Instant, typeof(EmptyPkAccessorDb))]
            public readonly partial record struct Widget([PrimaryKey(Accessor = "")] int Id, string Name);
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO002"));
    }

    [Test]
    public void EmptyTableAccessor_ReportsRHINO002() {
        const string source = """
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class EmptyTableAccessorDb : DbContext<EmptyTableAccessorDbTransaction> { }

            [Table(TableKind.Instant, typeof(EmptyTableAccessorDb), Accessor = "")]
            public readonly partial record struct Widget([PrimaryKey] int Id, string Name);
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO002"));
    }

    [Test]
    public void CompositeIndex_MismatchedKindOrUniqueness_ReportsRHINO003() {
        const string source = """
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class MismatchDb : DbContext<MismatchDbTransaction> { }

            [Table(TableKind.Instant, typeof(MismatchDb))]
            public readonly partial record struct Fixture(
                [PrimaryKey] int Id,
                [Index(IndexKind.Hash, Uniqueness.Unique, Accessor = "HomeAway")] int HomeClubId,
                [Index(IndexKind.Hash, Uniqueness.NonUnique, Accessor = "HomeAway")] int AwayClubId);
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO003"));
    }

    [Test]
    public void CompositeIndex_MoreThanThreeFields_ReportsRHINO004() {
        const string source = """
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class TooManyDb : DbContext<TooManyDbTransaction> { }

            [Table(TableKind.Instant, typeof(TooManyDb))]
            public readonly partial record struct Wide(
                [PrimaryKey] int Id,
                [Index(IndexKind.Hash, Accessor = "Combo")] int A,
                [Index(IndexKind.Hash, Accessor = "Combo")] int B,
                [Index(IndexKind.Hash, Accessor = "Combo")] int C,
                [Index(IndexKind.Hash, Accessor = "Combo")] int D);
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO004"));
    }

    [Test]
    public void CompositeIndex_DuplicateExplicitOrder_ReportsRHINO005() {
        const string source = """
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class DupOrderDb : DbContext<DupOrderDbTransaction> { }

            [Table(TableKind.Instant, typeof(DupOrderDb))]
            public readonly partial record struct Fixture(
                [PrimaryKey] int Id,
                [Index(IndexKind.Hash, Uniqueness.Unique, Accessor = "HomeAway", Order = 0)] int HomeClubId,
                [Index(IndexKind.Hash, Uniqueness.Unique, Accessor = "HomeAway", Order = 0)] int AwayClubId);
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO005"));
    }
}
