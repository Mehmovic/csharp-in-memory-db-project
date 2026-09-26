using RhinoDB.PreBuild;
using RhinoDB.SchemaContracts;

namespace RhinoDB.PreBuild.Test;

public class ShorthandExpanderTests {
    static private readonly Dictionary<string, string> NoProjectSources = new();

    [Test]
    public void Expand_VersionedMemoryPackProtocol_ReferenceTypedField_UsesVersionTolerantAndNoMessagePack() {
        var shorthand = """
            using RhinoDB.PreBuild.Shorthand;

            namespace TestNs;

            [InstantTable(typeof(GameDb))]
            public readonly partial record struct Metric(
                [PrimaryKey] int Id,
                uint Count,
                long Timestamp,
                decimal Amount,
                string Label
            );
            """;

        var expanded = ShorthandExpander.Expand(shorthand, NoProjectSources, ClientProtocolKind.VersionedMemoryPack);

        var expected = """
            using MemoryPack;
            using MessagePack;
            using RhinoDB.Core.Tables;
            using RhinoDB.PreBuild.Shorthand;

            namespace TestNs;

            [Table(TableKind.Instant, typeof(GameDb))]
            [MemoryPackable(GenerateType.VersionTolerant)]
            public readonly partial record struct Metric(
                [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
                [property: MemoryPackOrder(1)] [property: Key(1)] uint Count,
                [property: MemoryPackOrder(2)] [property: Key(2)] long Timestamp,
                [property: MemoryPackOrder(3)] [property: Key(3)] decimal Amount,
                [property: MemoryPackOrder(4)] [property: Key(4)] string Label
            );

            """;

        Assert.That(Normalize(expanded), Is.EqualTo(Normalize(expected)));
    }

    [Test]
    public void Expand_VersionedMemoryPackProtocol_FullyUnmanagedTable_UsesPlainMemoryPackable() {
        var shorthand = """
            namespace TestNs;

            [InstantTable(typeof(UnmanagedDb))]
            public readonly partial record struct Point(
                [PrimaryKey] int Id,
                int X,
                int Y
            );
            """;

        var expanded = ShorthandExpander.Expand(shorthand, NoProjectSources, ClientProtocolKind.VersionedMemoryPack);

        var expected = """
            using MemoryPack;
            using MessagePack;
            using RhinoDB.Core.Tables;

            namespace TestNs;

            [Table(TableKind.Instant, typeof(UnmanagedDb))]
            [MemoryPackable]
            public readonly partial record struct Point(
                [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
                [property: MemoryPackOrder(1)] [property: Key(1)] int X,
                [property: MemoryPackOrder(2)] [property: Key(2)] int Y
            );

            """;

        Assert.That(Normalize(expanded), Is.EqualTo(Normalize(expected)));
    }

    [Test]
    public void Expand_MessagePackProtocol_EmitsOnlyMessagePackObject() {
        var shorthand = """
            namespace TestNs;

            [InstantTable(typeof(GameDb))]
            public readonly partial record struct Point(
                [PrimaryKey] int Id,
                int X
            );
            """;

        var expanded = ShorthandExpander.Expand(shorthand, NoProjectSources, ClientProtocolKind.MessagePack);

        Assert.That(expanded, Does.Contain("[MessagePackObject]"));
        Assert.That(expanded, Does.Not.Contain("[MemoryPackable"));
    }

    [Test]
    public void Expand_RawProtocol_EmitsNeitherMandatoryAttribute() {
        var shorthand = """
            namespace TestNs;

            [InstantTable(typeof(GameDb))]
            public readonly partial record struct Point(
                [PrimaryKey] int Id,
                int X
            );
            """;

        var expanded = ShorthandExpander.Expand(shorthand, NoProjectSources, ClientProtocolKind.Raw);

        Assert.That(expanded, Does.Not.Contain("[MemoryPackable"));
        Assert.That(expanded, Does.Not.Contain("[MessagePackObject]"));
    }

    [Test]
    public void Expand_PersistentTableWithNamedArguments_PassesThemThroughOnTheExpandedTableAttribute() {
        var shorthand = """
            namespace TestNs;

            [PersistentTable(typeof(GameDb), Accessor = "Widgets", ChunkSize = 8192)]
            public readonly partial record struct PersistentSample(
                [PrimaryKey] int Id,
                long Value
            );
            """;

        var expanded = ShorthandExpander.Expand(shorthand, NoProjectSources, ClientProtocolKind.Raw);

        Assert.That(expanded, Does.Contain("[Table(TableKind.Persistent, typeof(GameDb), Accessor = \"Widgets\", ChunkSize = 8192)]"));
    }

    [Test]
    public void Expand_RhinoType_EmitsCustomTypeInsteadOfTable() {
        var shorthand = """
            namespace TestNs;

            [RhinoType]
            public readonly partial record struct PlayerName(
                string First,
                string Last
            );
            """;

        var expanded = ShorthandExpander.Expand(shorthand, NoProjectSources, ClientProtocolKind.VersionedMemoryPack);

        Assert.That(expanded, Does.Contain("[CustomType]"));
        Assert.That(expanded, Does.Not.Contain("[Table("));
        Assert.That(expanded, Does.Contain("GenerateType.VersionTolerant"));
    }

    [Test]
    public void Expand_RhinoTypeReferencingAFullyUnmanagedCustomType_ResolvesUnmanagednessAcrossProjectSources() {
        var projectSources = new Dictionary<string, string> {
            ["FixedPoint.cs"] = """
                [CustomType]
                public readonly partial record struct FixedPoint(int Whole, int Fraction);
                """
        };

        var shorthand = """
            namespace TestNs;

            [RhinoType]
            public readonly partial record struct Position(
                FixedPoint X,
                FixedPoint Y
            );
            """;

        var expanded = ShorthandExpander.Expand(shorthand, projectSources, ClientProtocolKind.VersionedMemoryPack);

        Assert.That(expanded, Does.Contain("[MemoryPackable]"));
        Assert.That(expanded, Does.Not.Contain("GenerateType.VersionTolerant"));
    }

    static private string Normalize(string text) => text.Replace("\r\n", "\n").Trim();
}
