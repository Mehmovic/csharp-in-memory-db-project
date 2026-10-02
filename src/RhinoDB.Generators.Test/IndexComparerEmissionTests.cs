using NUnit.Framework;

namespace RhinoDB.Generators.Test;

// Proves the generator EMITS an ordinal comparer for BTree index keys that contain a
// string, and emits nothing for keys that do not.
//
// This closes a gap that behavioural tests cannot: a string index built with the default
// comparer still works - it is merely 7x slower and orders rows by the host's culture, so
// two servers can scan the same data in different orders. Every runtime test passes either
// way, which is exactly why the emitted ctor argument has to be asserted directly.
[TestFixture]
public class IndexComparerEmissionTests {
    const string Source = """
        using MemoryPack;
        using MessagePack;
        using RhinoDB.Core.Tables;
        using RhinoDB.Lib.Execution;

        namespace TestNs;

        [Database]
        public partial class CompDb : DbContext<CompDbTransaction> { }

        // A string BTree PRIMARY key - the single-string ordinal case. A Hash primary key takes no comparer, so the kind has to be explicit for this to mean anything.
        [Table(TableKind.Instant, typeof(CompDb))]
        [MemoryPackable(GenerateType.VersionTolerant)]
        [MessagePackObject]
        public readonly partial record struct Team(
            [PrimaryKey(IndexKind.BTree)] [property: MemoryPackOrder(0)] [property: Key(0)] string Name);

        // Secondary indexes over string, composite, and a non-string key for contrast.
        [Table(TableKind.Instant, typeof(CompDb))]
        [MemoryPackable(GenerateType.VersionTolerant)]
        [MessagePackObject]
        public readonly partial record struct Player(
            [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
            [Index(IndexKind.BTree, Uniqueness.Unique, Accessor = "ByName", Order = 0)] [property: MemoryPackOrder(1)] [property: Key(1)] string Name,
            [Index(IndexKind.Hash, Uniqueness.Unique, Accessor = "ByClubHash", Order = 0)] [property: MemoryPackOrder(2)] [property: Key(2)] int ClubId,
            [Index(IndexKind.BTree, Uniqueness.NonUnique, Accessor = "ByClubThenRegion", Order = 0)] [property: MemoryPackOrder(3)] [property: Key(3)] int ClubId2,
            [Index(IndexKind.BTree, Uniqueness.NonUnique, Accessor = "ByClubThenRegion", Order = 1)] [property: MemoryPackOrder(4)] [property: Key(4)] string Region);
    """;
    static string Emit() {
        var generated = GeneratorTestHost.RunTableGenerator(Source, nameof(IndexComparerEmissionTests));
        Assert.That(generated, Is.Not.Empty, "TableGenerator emitted nothing - the harness is broken, not the assertion.");
        return string.Join("\n", generated.Values);
    }

    [Test]
    public void ASingleStringPrimaryKey_IsBuiltWithStringComparerOrdinal() {
        Assert.That(Emit(), Does.Contain("teamPrimaryIndex = new(comparer: StringComparer.Ordinal)"),
            "A string primary key is the most important ordinal case, and the index is named after the table.");
    }

    [Test]
    public void ASingleStringSecondaryBTreeIndex_IsBuiltWithStringComparerOrdinal() {
        Assert.That(Emit(), Does.Contain("new(comparer: StringComparer.Ordinal)"),
            "A string-keyed secondary BTree index must also be ordinal.");
    }

    [Test]
    public void AHashIndexOnAStringKey_GetsNoComparerArgument() {
        // HashIndex takes no comparer parameter - passing one would not compile, so this
        // asserts the generator correctly withholds it rather than over-applying.
        var emitted = Emit();
        Assert.That(emitted, Does.Contain("ByClubHashIndex = new()"));
    }

    [Test]
    public void ACompositeKeyWithAStringPart_GetsAnOrdinalTupleComparer() {
        var emitted = Emit();
        Assert.That(emitted, Does.Contain("OrdinalComparers.For2<int, string>()"),
            "A composite (int, string) key must use the ordinal tuple comparer.");
    }

    [Test]
    public void NoIndexIsEverBuiltWithTheCultureAwareDefault() {
        // The regression this guards: a tuple key silently falling back to
        // Comparer<string>.Default, which is culture-sensitive and culture-dependent.
        var emitted = Emit();
        Assert.That(emitted, Does.Not.Contain("ByClubThenNameIndex = new()"),
            "A composite key containing a string must NOT be left on the default comparer.");
    }
}
