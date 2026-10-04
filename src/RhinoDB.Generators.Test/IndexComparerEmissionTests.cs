using NUnit.Framework;

namespace RhinoDB.Generators.Test;

// Proves the generator EMITS an ordinal struct comparer as the TCmp type argument of every
// BTree index whose key contains a string, and DefaultComparer<T> for keys that do not.
//
// This closes a gap that behavioural tests cannot: a string index built with the default
// comparer still works - it is merely 7x slower and orders rows by the host's culture, so
// two servers can scan the same data in different orders. Every runtime test passes either
// way, which is exactly why the emitted comparer type argument has to be asserted directly.
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
        [Table<CompDb>(TableKind.Instant)]
        [MemoryPackable(GenerateType.VersionTolerant)]
        [MessagePackObject]
        public readonly partial record struct Team(
            [PrimaryKey(IndexKind.BTree)] [property: MemoryPackOrder(0)] [property: Key(0)] string Name);

        // Secondary indexes over string, composite, and a non-string key for contrast.
        [Table<CompDb>(TableKind.Instant)]
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
    public void ASingleStringPrimaryKey_IsBuiltWithTheOrdinalStringComparer() {
        Assert.That(Emit(), Does.Contain("BTreeIndex<string, OrdinalStringComparer> teamPrimaryIndex = new()"),
            "A string primary key is the most important ordinal case, and the index is named after the table.");
    }

    [Test]
    public void ASingleStringSecondaryBTreeIndex_IsBuiltWithTheOrdinalStringComparer() {
        Assert.That(Emit(), Does.Contain("BTreeIndex<string, OrdinalStringComparer> playerByNameIndex"),
            "A string-keyed secondary BTree index must also be ordinal.");
    }

    [Test]
    public void ANonStringBTreeKey_IsBuiltWithTheDefaultStructComparer() {
        Assert.That(Emit(), Does.Contain("DefaultComparer<int>"),
            "Non-string keys still need a struct comparer type argument so compares are inlined.");
    }

    [Test]
    public void AHashIndexOnAStringKey_GetsNoComparerArgument() {
        // HashIndex takes no comparer - it must stay a single-type-argument HashIndex.
        var emitted = Emit();
        Assert.That(emitted, Does.Contain("ByClubHashIndex = new()"));
        Assert.That(emitted, Does.Contain("HashIndex<int> playerByClubHashIndex"));
    }

    [Test]
    public void ACompositeKeyWithAStringPart_GetsAnOrdinalTupleComparer() {
        var emitted = Emit();
        Assert.That(emitted, Does.Contain("TupleComparer<int, string, DefaultComparer<int>, OrdinalStringComparer>"),
            "A composite (int, string) key must use the ordinal tuple comparer.");
    }

    [Test]
    public void NoIndexIsEverBuiltWithTheCultureAwareDefault() {
        // The regression this guards: a string, or a tuple containing one, silently falling
        // back to Comparer<string>.Default, which is culture-sensitive and culture-dependent.
        var emitted = Emit();
        Assert.That(emitted, Does.Not.Contain("DefaultComparer<string>"));
        Assert.That(emitted, Does.Not.Contain("DefaultComparer<(int ClubId2, string Region)>"),
            "A composite key containing a string must NOT be left on the default comparer.");
    }
}
