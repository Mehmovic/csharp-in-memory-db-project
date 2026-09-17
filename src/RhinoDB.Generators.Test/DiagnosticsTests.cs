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

    [Test]
    public void TableIdCollision_ReportsRHINO011() {
        // FNV-1a is 32 bits wide, so two Accessors can collide - found by brute force and pinned
        // here as a precondition, so the pair can never silently stop colliding. A collision would
        // make ColdStore.OpenTable overwrite a tableDbisById entry silently and would misroute WAL
        // recovery data, so it has to fail the build rather than surface at runtime.
        Assert.That(RhinoDB.Lib.Durability.TableIdHash.Compute("Tblj3vu"), Is.EqualTo(1420640043u), "Precondition: this pair must collide under the runtime hash.");
        Assert.That(RhinoDB.Lib.Durability.TableIdHash.Compute("Tbl4tea"), Is.EqualTo(1420640043u), "Precondition: this pair must collide under the runtime hash.");

        const string source = """
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class CollideDb : DbContext<CollideDbTransaction> { }

            [Table(TableKind.Persistent, typeof(CollideDb), Accessor = "Tblj3vu")]
            public readonly partial record struct Widget([PrimaryKey] int Id, string Name);

            [Table(TableKind.Persistent, typeof(CollideDb), Accessor = "Tbl4tea")]
            public readonly partial record struct Gadget([PrimaryKey] int Id, string Name);
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO011"));
    }

    [Test]
    public void TwoIndexesOnOneField_DefaultAccessorsCollide_ReportsRHINO012() {
        const string source = """
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class SameFieldDb : DbContext<SameFieldDbTransaction> { }

            [Table(TableKind.Instant, typeof(SameFieldDb))]
            public readonly partial record struct Player(
                [PrimaryKey] int Id,
                [Index(IndexKind.Hash, Uniqueness.NonUnique)]
                [Index(IndexKind.BTree, Uniqueness.NonUnique)]
                int ClubId);
            """;

        // Both attributes default their Accessor to the field name, so the two
        // indexes would silently collapse into one - a diagnostic, not a guess.
        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO012"));
    }

    [Test]
    public void TwoIndexesOnOneField_SameExplicitAccessor_ReportsRHINO012() {
        const string source = """
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class SameAccessorDb : DbContext<SameAccessorDbTransaction> { }

            [Table(TableKind.Instant, typeof(SameAccessorDb))]
            public readonly partial record struct Player(
                [PrimaryKey] int Id,
                [Index(IndexKind.Hash, Uniqueness.NonUnique, Accessor = "ByClub")]
                [Index(IndexKind.BTree, Uniqueness.NonUnique, Accessor = "ByClub")]
                int ClubId);
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO012"));
    }

    [Test]
    public void IndexAccessorNamedLikeThePrimaryKeyAccessor_ReportsRHINO013() {
        const string source = """
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class TakenNameDb : DbContext<TakenNameDbTransaction> { }

            [Table(TableKind.Instant, typeof(TakenNameDb))]
            public readonly partial record struct Player(
                [PrimaryKey] int Id,
                [Index(IndexKind.Hash, Uniqueness.NonUnique, Accessor = "Find")] int ClubId);
            """;

        // The primary key accessor defaults to "Find" - an index accessor of the
        // same name would emit a second Find(int) on the ops class.
        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO013"));
    }

    [Test]
    public void IndexAccessorNamedLikeAnOpsMethod_ReportsRHINO013() {
        const string source = """
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class OpsNameDb : DbContext<OpsNameDbTransaction> { }

            [Table(TableKind.Instant, typeof(OpsNameDb))]
            public readonly partial record struct Player(
                [PrimaryKey] int Id,
                [Index(IndexKind.Hash, Uniqueness.NonUnique, Accessor = "Delete")] int ClubId);
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO013"));
    }

    [Test]
    public void IndexAccessorsDifferingOnlyInFirstLetterCasing_ReportsRHINO014() {
        const string source = """
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class CasingDb : DbContext<CasingDbTransaction> { }

            [Table(TableKind.Instant, typeof(CasingDb))]
            public readonly partial record struct Player(
                [PrimaryKey] int Id,
                [Index(IndexKind.Hash, Uniqueness.NonUnique, Accessor = "ByClub")] int ClubId,
                [Index(IndexKind.Hash, Uniqueness.NonUnique, Accessor = "byClub")] int ShirtNumber);
            """;

        // Both camel-case to "byClub", so both would emit the field byClubIndex.
        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO014"));
    }

    [Test]
    public void IndexAccessorsDifferingBeyondTheFirstLetter_AreAccepted() {
        const string source = """
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class DistinctDb : DbContext<DistinctDbTransaction> { }

            [Table(TableKind.Instant, typeof(DistinctDb))]
            public readonly partial record struct Player(
                [PrimaryKey] int Id,
                [Index(IndexKind.Hash, Uniqueness.NonUnique, Accessor = "ByClub")] int ClubId,
                [Index(IndexKind.Hash, Uniqueness.NonUnique, Accessor = "ByShirt")] int ShirtNumber);
            """;

        // Contrast case: the reserved-name and casing checks must not reject
        // ordinary, distinct accessor names.
        Assert.DoesNotThrow(() => GeneratorTestHost.CompileAndLoad(source));
    }
}
