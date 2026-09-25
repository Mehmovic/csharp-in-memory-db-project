using System.Collections.Immutable;

using Microsoft.CodeAnalysis;

using RhinoDB.SchemaContracts;

namespace RhinoDB.SchemaContracts.Test;

public class DescriptorBuilderReverseMapTests {
    const string Prelude = """
        namespace RhinoDB.Core.Tables { public class CustomTypeAttribute : System.Attribute { } }
        namespace MemoryPack { public class MemoryPackableAttribute : System.Attribute { } }
        namespace MessagePack { public class MessagePackObjectAttribute : System.Attribute { } }
        """;

    static ImmutableArray<IParameterSymbol> PrimaryCtorParams(INamedTypeSymbol type) =>
        SchemaWalk.FindPrimaryConstructor(type)!.Parameters;

    [Test]
    public void BuildReverseMap_TypeSharedDirectlyByTwoTables_MapsToBoth() {
        const string source = Prelude + """
            namespace TestNs;

            [RhinoDB.Core.Tables.CustomType]
            [MemoryPack.MemoryPackable]
            [MessagePack.MessagePackObject]
            public readonly partial record struct Loadout(int WeaponId);

            [MemoryPack.MemoryPackable]
            [MessagePack.MessagePackObject]
            public readonly partial record struct Player(int Id, Loadout Loadout);

            [MemoryPack.MemoryPackable]
            [MessagePack.MessagePackObject]
            public readonly partial record struct Npc(int Id, Loadout Loadout);
            """;
        var playerType = CompilationHelper.GetType(source, "TestNs.Player");
        var npcType = CompilationHelper.GetType(source, "TestNs.Npc");

        var map = DescriptorBuilder.BuildReverseMap([
            ("TestNs.GameDb", "Player", PrimaryCtorParams(playerType)),
            ("TestNs.GameDb", "Npc", PrimaryCtorParams(npcType)),
        ]);

        Assert.That(map["global::TestNs.Loadout"], Is.EquivalentTo(new[] {
            ("TestNs.GameDb", "Player"),
            ("TestNs.GameDb", "Npc"),
        }));
    }

    [Test]
    public void BuildReverseMap_TypeEmbeddedTransitivelyThroughAnotherCustomType_StillMapsToTheOutermostTable() {
        const string source = Prelude + """
            namespace TestNs;

            [RhinoDB.Core.Tables.CustomType]
            [MemoryPack.MemoryPackable]
            [MessagePack.MessagePackObject]
            public readonly partial record struct Cosmetics(int HatId);

            [RhinoDB.Core.Tables.CustomType]
            [MemoryPack.MemoryPackable]
            [MessagePack.MessagePackObject]
            public readonly partial record struct Loadout(int WeaponId, Cosmetics Cosmetics);

            [MemoryPack.MemoryPackable]
            [MessagePack.MessagePackObject]
            public readonly partial record struct Player(int Id, Loadout Loadout);
            """;
        var playerType = CompilationHelper.GetType(source, "TestNs.Player");

        var map = DescriptorBuilder.BuildReverseMap([
            ("TestNs.GameDb", "Player", PrimaryCtorParams(playerType)),
        ]);

        Assert.That(map["global::TestNs.Loadout"], Is.EquivalentTo(new[] { ("TestNs.GameDb", "Player") }));
        Assert.That(map["global::TestNs.Cosmetics"], Is.EquivalentTo(new[] { ("TestNs.GameDb", "Player") }),
            "a CustomType nested inside another CustomType must still resolve to the outermost table that (transitively) embeds it.");
    }

    [Test]
    public void BuildReverseMap_TypeUsedByTablesAcrossDifferentDatabases_MapsToBothDatabases() {
        // The whole reason the reverse map has to span the whole compilation (point A) - one CustomType
        // shared by tables belonging to two different [Database]s, since a row type's [Table] attributes
        // are necessarily co-located with its own declaration regardless of how many databases use it.
        const string source = Prelude + """
            namespace TestNs;

            [RhinoDB.Core.Tables.CustomType]
            [MemoryPack.MemoryPackable]
            [MessagePack.MessagePackObject]
            public readonly partial record struct Address(string City);

            [MemoryPack.MemoryPackable]
            [MessagePack.MessagePackObject]
            public readonly partial record struct Player(int Id, Address Address);

            [MemoryPack.MemoryPackable]
            [MessagePack.MessagePackObject]
            public readonly partial record struct AdminPlayer(int Id, Address Address);
            """;
        var playerType = CompilationHelper.GetType(source, "TestNs.Player");
        var adminPlayerType = CompilationHelper.GetType(source, "TestNs.AdminPlayer");

        var map = DescriptorBuilder.BuildReverseMap([
            ("TestNs.GameDb", "Player", PrimaryCtorParams(playerType)),
            ("TestNs.AdminDb", "AdminPlayer", PrimaryCtorParams(adminPlayerType)),
        ]);

        Assert.That(map["global::TestNs.Address"], Is.EquivalentTo(new[] {
            ("TestNs.GameDb", "Player"),
            ("TestNs.AdminDb", "AdminPlayer"),
        }));
    }

    [Test]
    public void BuildReverseMap_NoCustomTypeFieldsAnywhere_ReturnsEmptyMap() {
        const string source = Prelude + """
            namespace TestNs;

            [MemoryPack.MemoryPackable]
            [MessagePack.MessagePackObject]
            public readonly partial record struct Widget(int Id, string Name);
            """;
        var widgetType = CompilationHelper.GetType(source, "TestNs.Widget");

        var map = DescriptorBuilder.BuildReverseMap([("TestNs.GameDb", "Widget", PrimaryCtorParams(widgetType))]);

        Assert.That(map, Is.Empty);
    }
}
