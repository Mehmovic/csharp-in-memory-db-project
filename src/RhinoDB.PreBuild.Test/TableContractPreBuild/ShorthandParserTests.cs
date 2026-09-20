using RhinoDB.PreBuild;

namespace RhinoDB.Test.PreBuild;

public class ShorthandParserTests {
    [Test]
    public void Parse_InstantTableWithUnmanagedFields_ExtractsKindDatabaseAndFields() {
        var parsed = ShorthandParser.Parse("""
            using RhinoDB.PreBuild.Shorthand;

            namespace TestNs;

            [InstantTable(typeof(GameDb))]
            public readonly partial record struct InstantSample(
                [PrimaryKey] int Id,
                long Value
            );
            """);

        Assert.That(parsed.Namespace, Is.EqualTo("TestNs"));
        Assert.That(parsed.UsingDirectives, Does.Contain("RhinoDB.PreBuild.Shorthand"));
        Assert.That(parsed.TypeName, Is.EqualTo("InstantSample"));
        Assert.That(parsed.Kind, Is.EqualTo(ShorthandKind.InstantTable));
        Assert.That(parsed.DatabaseTypeName, Is.EqualTo("GameDb"));
        Assert.That(parsed.TableNamedArguments, Is.Empty);
        Assert.That(parsed.Fields, Has.Length.EqualTo(2));
        Assert.That(parsed.Fields[0].Name, Is.EqualTo("Id"));
        Assert.That(parsed.Fields[0].TypeName, Is.EqualTo("int"));
        Assert.That(parsed.Fields[0].PassThroughAttributes, Does.Contain("[PrimaryKey]"));
        Assert.That(parsed.Fields[0].ExplicitPackId, Is.Null);
        Assert.That(parsed.Fields[1].Name, Is.EqualTo("Value"));
        Assert.That(parsed.Fields[1].TypeName, Is.EqualTo("long"));
        Assert.That(parsed.Fields[1].PassThroughAttributes, Is.Empty);
    }

    [Test]
    public void Parse_PersistentTableWithNamedArguments_PassesThemThroughVerbatim() {
        var parsed = ShorthandParser.Parse("""
            namespace TestNs;

            [PersistentTable(typeof(GameDb), Accessor = "Widgets", ChunkSize = 8192, Evictable = true)]
            public readonly partial record struct PersistentSample(
                [PrimaryKey] int Id,
                [PackId(0)] long Value
            );
            """);

        Assert.That(parsed.Kind, Is.EqualTo(ShorthandKind.PersistentTable));
        Assert.That(parsed.DatabaseTypeName, Is.EqualTo("GameDb"));
        Assert.That(parsed.TableNamedArguments, Is.EquivalentTo(new[] {
            ("Accessor", "\"Widgets\""),
            ("ChunkSize", "8192"),
            ("Evictable", "true"),
        }));
        Assert.That(parsed.Fields[1].ExplicitPackId, Is.EqualTo(0));
    }

    [Test]
    public void Parse_RhinoType_HasNoDatabaseAndNoNamedArguments() {
        var parsed = ShorthandParser.Parse("""
            namespace TestNs;

            [RhinoType]
            public readonly partial record struct PlayerName(
                string First,
                string Last
            );
            """);

        Assert.That(parsed.Kind, Is.EqualTo(ShorthandKind.RhinoType));
        Assert.That(parsed.DatabaseTypeName, Is.Null);
        Assert.That(parsed.TableNamedArguments, Is.Empty);
        Assert.That(parsed.Fields, Has.Length.EqualTo(2));
    }

    [Test]
    public void Parse_ExplicitNegativePackId_ParsesTheNegativeValue() {
        var parsed = ShorthandParser.Parse("""
            namespace TestNs;

            [RhinoType]
            public readonly partial record struct Foo(
                [PackId(-1)] int A,
                [PackId(0)] int B
            );
            """);

        Assert.That(parsed.Fields[0].ExplicitPackId, Is.EqualTo(-1));
        Assert.That(parsed.Fields[1].ExplicitPackId, Is.EqualTo(0));
    }

    [Test]
    public void Parse_MultiplePassThroughAttributesOnOneField_KeepsAllOfThemSeparately() {
        var parsed = ShorthandParser.Parse("""
            namespace TestNs;

            [InstantTable(typeof(GameDb))]
            public readonly partial record struct Foo(
                [PrimaryKey] [AutoIncrement] int Id
            );
            """);

        Assert.That(parsed.Fields[0].PassThroughAttributes, Is.EquivalentTo(new[] { "[PrimaryKey]", "[AutoIncrement]" }));
    }

    [Test]
    public void Parse_NoShorthandAttribute_Throws() {
        Assert.Throws<ShorthandParseException>(() => ShorthandParser.Parse("""
            namespace TestNs;

            public readonly partial record struct Foo(int Id);
            """));
    }

    [Test]
    public void Parse_NoPrimaryConstructor_Throws() {
        Assert.Throws<ShorthandParseException>(() => ShorthandParser.Parse("""
            namespace TestNs;

            [RhinoType]
            public readonly partial record struct Foo { public int Id { get; init; } }
            """));
    }

    [Test]
    public void Parse_TrailingCommaInParameterList_ThrowsInsteadOfProducingAPhantomField() {
        Assert.Throws<ShorthandParseException>(() => ShorthandParser.Parse("""
            namespace TestNs;

            [InstantTable(typeof(SandboxDb))]
            public readonly partial record struct PvPTable(
                [PrimaryKey] int Id,
                int Score,
                [Index(IndexKind.BTree)] string username,
            );
            """));
    }

    [Test]
    public void Parse_IndexAttributeWithConstructorAndNamedArguments_PassesThroughVerbatim() {
        var parsed = ShorthandParser.Parse("""
            namespace TestNs;

            [InstantTable(typeof(GameDb))]
            public readonly partial record struct Foo(
                [PrimaryKey] int Id,
                [Index(IndexKind.BTree, Uniqueness.Unique, Order = 2)] string Name
            );
            """);

        Assert.That(parsed.Fields[1].PassThroughAttributes,
            Does.Contain("[Index(IndexKind.BTree, Uniqueness.Unique, Order = 2)]"));
    }

    [Test]
    public void Parse_NoRecordStructAtAll_Throws() {
        Assert.Throws<ShorthandParseException>(() => ShorthandParser.Parse("""
            namespace TestNs;

            public class NotAShorthandType { }
            """));
    }
}
