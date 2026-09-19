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
            using MemoryPack;
            using MessagePack;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class EmptyAccessorDb : DbContext<EmptyAccessorDbTransaction> { }

            [Table(TableKind.Instant, typeof(EmptyAccessorDb))]
            [MemoryPackable(GenerateType.VersionTolerant)]
            [MessagePackObject]
            public readonly partial record struct Widget(
                [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
                [Index(IndexKind.Hash, Uniqueness.Unique, Accessor = "")] [property: MemoryPackOrder(1)] [property: Key(1)] string Sku);
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO002"));
    }

    [Test]
    public void EmptyPrimaryKeyAccessor_ReportsRHINO002() {
        const string source = """
            using MemoryPack;
            using MessagePack;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class EmptyPkAccessorDb : DbContext<EmptyPkAccessorDbTransaction> { }

            [Table(TableKind.Instant, typeof(EmptyPkAccessorDb))]
            [MemoryPackable(GenerateType.VersionTolerant)]
            [MessagePackObject]
            public readonly partial record struct Widget(
                [PrimaryKey(Accessor = "")] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
                [property: MemoryPackOrder(1)] [property: Key(1)] string Name);
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO002"));
    }

    [Test]
    public void EmptyTableAccessor_ReportsRHINO002() {
        const string source = """
            using MemoryPack;
            using MessagePack;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class EmptyTableAccessorDb : DbContext<EmptyTableAccessorDbTransaction> { }

            [Table(TableKind.Instant, typeof(EmptyTableAccessorDb), Accessor = "")]
            [MemoryPackable(GenerateType.VersionTolerant)]
            [MessagePackObject]
            public readonly partial record struct Widget(
                [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
                [property: MemoryPackOrder(1)] [property: Key(1)] string Name);
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO002"));
    }

    [Test]
    public void CompositeIndex_MismatchedKindOrUniqueness_ReportsRHINO003() {
        const string source = """
            using MemoryPack;
            using MessagePack;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class MismatchDb : DbContext<MismatchDbTransaction> { }

            [Table(TableKind.Instant, typeof(MismatchDb))]
            [MemoryPackable(GenerateType.VersionTolerant)]
            [MessagePackObject]
            public readonly partial record struct Fixture(
                [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
                [Index(IndexKind.Hash, Uniqueness.Unique, Accessor = "HomeAway")] [property: MemoryPackOrder(1)] [property: Key(1)] int HomeClubId,
                [Index(IndexKind.Hash, Uniqueness.NonUnique, Accessor = "HomeAway")] [property: MemoryPackOrder(2)] [property: Key(2)] int AwayClubId);
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO003"));
    }

    [Test]
    public void CompositeIndex_MoreThanThreeFields_ReportsRHINO004() {
        const string source = """
            using MemoryPack;
            using MessagePack;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class TooManyDb : DbContext<TooManyDbTransaction> { }

            [Table(TableKind.Instant, typeof(TooManyDb))]
            [MemoryPackable(GenerateType.VersionTolerant)]
            [MessagePackObject]
            public readonly partial record struct Wide(
                [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
                [Index(IndexKind.Hash, Accessor = "Combo")] [property: MemoryPackOrder(1)] [property: Key(1)] int A,
                [Index(IndexKind.Hash, Accessor = "Combo")] [property: MemoryPackOrder(2)] [property: Key(2)] int B,
                [Index(IndexKind.Hash, Accessor = "Combo")] [property: MemoryPackOrder(3)] [property: Key(3)] int C,
                [Index(IndexKind.Hash, Accessor = "Combo")] [property: MemoryPackOrder(4)] [property: Key(4)] int D);
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO004"));
    }

    [Test]
    public void CompositeIndex_DuplicateExplicitOrder_ReportsRHINO005() {
        const string source = """
            using MemoryPack;
            using MessagePack;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class DupOrderDb : DbContext<DupOrderDbTransaction> { }

            [Table(TableKind.Instant, typeof(DupOrderDb))]
            [MemoryPackable(GenerateType.VersionTolerant)]
            [MessagePackObject]
            public readonly partial record struct Fixture(
                [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
                [Index(IndexKind.Hash, Uniqueness.Unique, Accessor = "HomeAway", Order = 0)] [property: MemoryPackOrder(1)] [property: Key(1)] int HomeClubId,
                [Index(IndexKind.Hash, Uniqueness.Unique, Accessor = "HomeAway", Order = 0)] [property: MemoryPackOrder(2)] [property: Key(2)] int AwayClubId);
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
            using MemoryPack;
            using MessagePack;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class CollideDb : DbContext<CollideDbTransaction> { }

            [Table(TableKind.Persistent, typeof(CollideDb), Accessor = "Tblj3vu")]
            [MemoryPackable(GenerateType.VersionTolerant)]
            [MessagePackObject]
            public readonly partial record struct Widget(
                [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
                [property: MemoryPackOrder(1)] [property: Key(1)] string Name);

            [Table(TableKind.Persistent, typeof(CollideDb), Accessor = "Tbl4tea")]
            [MemoryPackable(GenerateType.VersionTolerant)]
            [MessagePackObject]
            public readonly partial record struct Gadget(
                [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
                [property: MemoryPackOrder(1)] [property: Key(1)] string Name);
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO011"));
    }

    [Test]
    public void TwoIndexesOnOneField_DefaultAccessorsCollide_ReportsRHINO012() {
        const string source = """
            using MemoryPack;
            using MessagePack;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class SameFieldDb : DbContext<SameFieldDbTransaction> { }

            [Table(TableKind.Instant, typeof(SameFieldDb))]
            [MemoryPackable(GenerateType.VersionTolerant)]
            [MessagePackObject]
            public readonly partial record struct Player(
                [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
                [Index(IndexKind.Hash, Uniqueness.NonUnique)]
                [Index(IndexKind.BTree, Uniqueness.NonUnique)]
                [property: MemoryPackOrder(1)] [property: Key(1)] int ClubId);
            """;

        // Both attributes default their Accessor to the field name, so the two
        // indexes would silently collapse into one - a diagnostic, not a guess.
        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO012"));
    }

    [Test]
    public void TwoIndexesOnOneField_SameExplicitAccessor_ReportsRHINO012() {
        const string source = """
            using MemoryPack;
            using MessagePack;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class SameAccessorDb : DbContext<SameAccessorDbTransaction> { }

            [Table(TableKind.Instant, typeof(SameAccessorDb))]
            [MemoryPackable(GenerateType.VersionTolerant)]
            [MessagePackObject]
            public readonly partial record struct Player(
                [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
                [Index(IndexKind.Hash, Uniqueness.NonUnique, Accessor = "ByClub")]
                [Index(IndexKind.BTree, Uniqueness.NonUnique, Accessor = "ByClub")]
                [property: MemoryPackOrder(1)] [property: Key(1)] int ClubId);
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO012"));
    }

    [Test]
    public void IndexAccessorNamedLikeThePrimaryKeyAccessor_ReportsRHINO013() {
        const string source = """
            using MemoryPack;
            using MessagePack;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class TakenNameDb : DbContext<TakenNameDbTransaction> { }

            [Table(TableKind.Instant, typeof(TakenNameDb))]
            [MemoryPackable(GenerateType.VersionTolerant)]
            [MessagePackObject]
            public readonly partial record struct Player(
                [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
                [Index(IndexKind.Hash, Uniqueness.NonUnique, Accessor = "Find")] [property: MemoryPackOrder(1)] [property: Key(1)] int ClubId);
            """;

        // The primary key accessor defaults to "Find" - an index accessor of the
        // same name would emit a second Find(int) on the ops class.
        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO013"));
    }

    [Test]
    public void IndexAccessorNamedLikeAnOpsMethod_ReportsRHINO013() {
        const string source = """
            using MemoryPack;
            using MessagePack;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class OpsNameDb : DbContext<OpsNameDbTransaction> { }

            [Table(TableKind.Instant, typeof(OpsNameDb))]
            [MemoryPackable(GenerateType.VersionTolerant)]
            [MessagePackObject]
            public readonly partial record struct Player(
                [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
                [Index(IndexKind.Hash, Uniqueness.NonUnique, Accessor = "Delete")] [property: MemoryPackOrder(1)] [property: Key(1)] int ClubId);
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO013"));
    }

    [Test]
    public void IndexAccessorsDifferingOnlyInFirstLetterCasing_ReportsRHINO014() {
        const string source = """
            using MemoryPack;
            using MessagePack;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class CasingDb : DbContext<CasingDbTransaction> { }

            [Table(TableKind.Instant, typeof(CasingDb))]
            [MemoryPackable(GenerateType.VersionTolerant)]
            [MessagePackObject]
            public readonly partial record struct Player(
                [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
                [Index(IndexKind.Hash, Uniqueness.NonUnique, Accessor = "ByClub")] [property: MemoryPackOrder(1)] [property: Key(1)] int ClubId,
                [Index(IndexKind.Hash, Uniqueness.NonUnique, Accessor = "byClub")] [property: MemoryPackOrder(2)] [property: Key(2)] int ShirtNumber);
            """;

        // Both camel-case to "byClub", so both would emit the field byClubIndex.
        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO014"));
    }

    [Test]
    public void IndexAccessorsDifferingBeyondTheFirstLetter_AreAccepted() {
        const string source = """
            using MemoryPack;
            using MessagePack;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class DistinctDb : DbContext<DistinctDbTransaction> { }

            [Table(TableKind.Instant, typeof(DistinctDb))]
            [MemoryPackable(GenerateType.VersionTolerant)]
            [MessagePackObject]
            public readonly partial record struct Player(
                [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
                [Index(IndexKind.Hash, Uniqueness.NonUnique, Accessor = "ByClub")] [property: MemoryPackOrder(1)] [property: Key(1)] int ClubId,
                [Index(IndexKind.Hash, Uniqueness.NonUnique, Accessor = "ByShirt")] [property: MemoryPackOrder(2)] [property: Key(2)] int ShirtNumber);
            """;

        // Contrast case: the reserved-name and casing checks must not reject
        // ordinary, distinct accessor names.
        Assert.DoesNotThrow(() => GeneratorTestHost.CompileAndLoad(source));
    }

    [Test]
    public void MissingBothSerializationAttributes_ReportsRHINO015() {
        const string source = """
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class NoSerializationDb : DbContext<NoSerializationDbTransaction> { }

            [Table(TableKind.Instant, typeof(NoSerializationDb))]
            public readonly partial record struct Widget([PrimaryKey] int Id, string Name);
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO015"));
        Assert.That(ex.Message, Does.Contain("[MemoryPackable]"));
        Assert.That(ex.Message, Does.Contain("[MessagePackObject]"));
    }

    [Test]
    public void MissingOnlyMessagePackObject_ReportsRHINO015MentioningOnlyThatOne() {
        const string source = """
            using MemoryPack;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class HalfSerializationDb : DbContext<HalfSerializationDbTransaction> { }

            [Table(TableKind.Instant, typeof(HalfSerializationDb))]
            [MemoryPackable(GenerateType.VersionTolerant)]
            public readonly partial record struct Widget(
                [PrimaryKey] [property: MemoryPackOrder(0)] int Id,
                [property: MemoryPackOrder(1)] string Name);
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO015"));
        Assert.That(ex.Message, Does.Contain("[MessagePackObject]"));
        Assert.That(ex.Message, Does.Not.Contain("missing [MemoryPackable]"));
    }

    [Test]
    public void PlainMemoryPackableOnFullyUnmanagedRow_IsAccepted() {
        // VersionTolerant is rejected by MemoryPack's own generator for a fully-unmanaged row
        // (no reference-typed fields) - plain [MemoryPackable] (default GenerateType.Object) is
        // the correct, and only valid, choice there. The RHINO015 check must not require
        // VersionTolerant specifically, only that [MemoryPackable] (any mode) is present.
        const string source = """
            using MemoryPack;
            using MessagePack;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class UnmanagedDb : DbContext<UnmanagedDbTransaction> { }

            [Table(TableKind.Instant, typeof(UnmanagedDb))]
            [MemoryPackable]
            [MessagePackObject]
            public readonly partial record struct Point(
                [PrimaryKey] [property: Key(0)] int Id,
                [property: Key(1)] int X,
                [property: Key(2)] int Y);
            """;

        Assert.DoesNotThrow(() => GeneratorTestHost.CompileAndLoad(source));
    }

    [Test]
    public void CustomTypeFieldMissingBothSerializationAttributes_ReportsRHINO016() {
        const string source = """
            using MemoryPack;
            using MessagePack;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class CustomFieldDb : DbContext<CustomFieldDbTransaction> { }

            public readonly record struct PlayerName(string First, string Last);

            [Table(TableKind.Instant, typeof(CustomFieldDb))]
            [MemoryPackable(GenerateType.VersionTolerant)]
            [MessagePackObject]
            public readonly partial record struct Player(
                [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
                [property: MemoryPackOrder(1)] [property: Key(1)] PlayerName Name);
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO016"));
        Assert.That(ex.Message, Does.Contain("PlayerName"));
        Assert.That(ex.Message, Does.Contain("[CustomType]"));
        Assert.That(ex.Message, Does.Contain("[MemoryPackable]"));
        Assert.That(ex.Message, Does.Contain("[MessagePackObject]"));
    }

    [Test]
    public void CustomTypeFieldMissingOnlyTheCustomTypeAttribute_ReportsRHINO016() {
        // Only [MemoryPackable]/[MessagePackObject] present, no [CustomType] - no silent fallback to
        // MemoryPack's generic WriteValue<T>/ReadValue<T> dispatch is allowed; unmarked types are
        // rejected outright, even when they'd otherwise have a real formatter available.
        const string source = """
            using MemoryPack;
            using MessagePack;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class CustomFieldDb : DbContext<CustomFieldDbTransaction> { }

            [MemoryPackable(GenerateType.VersionTolerant)]
            [MessagePackObject]
            public readonly partial record struct PlayerName(
                [property: MemoryPackOrder(0)] [property: Key(0)] string First,
                [property: MemoryPackOrder(1)] [property: Key(1)] string Last);

            [Table(TableKind.Instant, typeof(CustomFieldDb))]
            [MemoryPackable(GenerateType.VersionTolerant)]
            [MessagePackObject]
            public readonly partial record struct Player(
                [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
                [property: MemoryPackOrder(1)] [property: Key(1)] PlayerName Name);
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO016"));
        // Exactly one item in the "missing" list - the explanatory text below always mentions
        // [MemoryPackable]/[MessagePackObject] regardless, so this checks the actual reported list itself.
        Assert.That(ex.Message, Does.Contain("which is missing [CustomType] -"));
    }

    [Test]
    public void CustomTypeFieldWithAllThreeAttributes_IsAccepted() {
        const string source = """
            using MemoryPack;
            using MessagePack;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class CustomFieldDb : DbContext<CustomFieldDbTransaction> { }

            [CustomType]
            [MemoryPackable(GenerateType.VersionTolerant)]
            [MessagePackObject]
            public readonly partial record struct PlayerName(
                [property: MemoryPackOrder(0)] [property: Key(0)] string First,
                [property: MemoryPackOrder(1)] [property: Key(1)] string Last);

            [Table(TableKind.Instant, typeof(CustomFieldDb))]
            [MemoryPackable(GenerateType.VersionTolerant)]
            [MessagePackObject]
            public readonly partial record struct Player(
                [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
                [property: MemoryPackOrder(1)] [property: Key(1)] PlayerName Name);
            """;

        Assert.DoesNotThrow(() => GeneratorTestHost.CompileAndLoad(source));
    }

    [Test]
    public void NestedCustomTypeWithinACustomTypeMissingTheCustomTypeAttribute_ReportsRHINO016Recursively() {
        // The check isn't hard-coded to recurse multiple levels - it only ever looks at a type's own
        // immediate fields. Recursive coverage falls out naturally instead: Contact is itself a
        // [CustomType], so CustomTypeGenerator independently runs the exact same check on Contact's OWN
        // fields (including HomeAddress) when it processes Contact as its own generator target - proving
        // an inner CustomType missing [CustomType] is caught even though the outer row (Player) never
        // looks past its own immediate field (Contact).
        const string source = """
            using MemoryPack;
            using MessagePack;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class CustomFieldDb : DbContext<CustomFieldDbTransaction> { }

            [MemoryPackable(GenerateType.VersionTolerant)]
            [MessagePackObject]
            public readonly partial record struct Address(
                [property: MemoryPackOrder(0)] [property: Key(0)] string City);

            [CustomType]
            [MemoryPackable(GenerateType.VersionTolerant)]
            [MessagePackObject]
            public readonly partial record struct Contact(
                [property: MemoryPackOrder(0)] [property: Key(0)] string Email,
                [property: MemoryPackOrder(1)] [property: Key(1)] Address HomeAddress);

            [Table(TableKind.Instant, typeof(CustomFieldDb))]
            [MemoryPackable(GenerateType.VersionTolerant)]
            [MessagePackObject]
            public readonly partial record struct Player(
                [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
                [property: MemoryPackOrder(1)] [property: Key(1)] Contact Contact);
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO016"));
        Assert.That(ex.Message, Does.Contain("Address"));
        Assert.That(ex.Message, Does.Contain("[CustomType]"));
    }

    [Test]
    public void ArrayTypedField_IsExemptFromTheCustomTypeCheck() {
        // Arrays are IArrayTypeSymbol, not INamedTypeSymbol - RHINO016 must not flag them, since
        // MemoryPack ships a global formatter for arrays of unmanaged types with no attribute needed
        // (see RawSerializerTests.cs's OtherKindField_BuiltInArrayFormatter test).
        const string source = """
            using MemoryPack;
            using MessagePack;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class ArrayFieldDb : DbContext<ArrayFieldDbTransaction> { }

            [Table(TableKind.Instant, typeof(ArrayFieldDb))]
            [MemoryPackable(GenerateType.VersionTolerant)]
            [MessagePackObject]
            public readonly partial record struct Widget(
                [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
                [property: MemoryPackOrder(1)] [property: Key(1)] int[] Scores);
            """;

        Assert.DoesNotThrow(() => GeneratorTestHost.CompileAndLoad(source));
    }

    [Test]
    public void BuiltInFormattedCollectionFields_AreExemptFromTheCustomTypeCheck() {
        // List/Dictionary/HashSet/Queue/Stack (BuiltInFormattedGenericCollections) are exempt the same
        // way arrays are - both MemoryPack and MessagePack ship global formatters for these, no
        // attribute needed (see RawSerializerTests.cs's
        // OtherKindField_BuiltInListAndDictionaryFormatters test for the round-trip proof).
        const string source = """
            using MemoryPack;
            using MessagePack;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;
            using System.Collections.Generic;

            namespace TestNs;

            [Database]
            public partial class CollectionFieldDb : DbContext<CollectionFieldDbTransaction> { }

            [Table(TableKind.Instant, typeof(CollectionFieldDb))]
            [MemoryPackable(GenerateType.VersionTolerant)]
            [MessagePackObject]
            public readonly partial record struct Inventory(
                [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
                [property: MemoryPackOrder(1)] [property: Key(1)] List<int> ItemIds,
                [property: MemoryPackOrder(2)] [property: Key(2)] Dictionary<int, string> ItemNames,
                [property: MemoryPackOrder(3)] [property: Key(3)] HashSet<int> Tags,
                [property: MemoryPackOrder(4)] [property: Key(4)] Queue<int> Pending,
                [property: MemoryPackOrder(5)] [property: Key(5)] Stack<int> Undo);
            """;

        Assert.DoesNotThrow(() => GeneratorTestHost.CompileAndLoad(source));
    }

    [Test]
    public void CustomTypeMissingBothSerializationAttributes_ReportsRHINO017() {
        const string source = """
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class NoSerializationDb : DbContext<NoSerializationDbTransaction> { }

            [CustomType]
            public readonly partial record struct PlayerName(string First, string Last);
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO017"));
        Assert.That(ex.Message, Does.Contain("[MemoryPackable]"));
        Assert.That(ex.Message, Does.Contain("[MessagePackObject]"));
    }

    [Test]
    public void CustomTypeMissingOnlyMessagePackObject_ReportsRHINO017MentioningOnlyThatOne() {
        const string source = """
            using MemoryPack;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class HalfSerializationDb : DbContext<HalfSerializationDbTransaction> { }

            [CustomType]
            [MemoryPackable(GenerateType.VersionTolerant)]
            public readonly partial record struct PlayerName(
                [property: MemoryPackOrder(0)] string First,
                [property: MemoryPackOrder(1)] string Last);
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO017"));
        Assert.That(ex.Message, Does.Contain("[MessagePackObject]"));
        Assert.That(ex.Message, Does.Not.Contain("missing [MemoryPackable]"));
    }

    [Test]
    public void CustomTypeWithBothSerializationAttributes_IsAccepted() {
        const string source = """
            using MemoryPack;
            using MessagePack;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class CustomTypeOkDb : DbContext<CustomTypeOkDbTransaction> { }

            [CustomType]
            [MemoryPackable(GenerateType.VersionTolerant)]
            [MessagePackObject]
            public readonly partial record struct PlayerName(
                [property: MemoryPackOrder(0)] [property: Key(0)] string First,
                [property: MemoryPackOrder(1)] [property: Key(1)] string Last);
            """;

        Assert.DoesNotThrow(() => GeneratorTestHost.CompileAndLoad(source));
    }
}
