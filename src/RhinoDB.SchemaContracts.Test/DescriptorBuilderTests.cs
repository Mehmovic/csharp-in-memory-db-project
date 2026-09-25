using RhinoDB.SchemaContracts;

namespace RhinoDB.SchemaContracts.Test;

public class DescriptorBuilderTests {
    // Shared stub attributes matching the exact fully-qualified names SchemaWalk checks for by string -
    // deliberately NOT the real RhinoDB.Core/MemoryPack/MessagePack packages, since SchemaWalk's checks
    // are purely name-based and this project doesn't reference those packages at all.
    const string Prelude = """
        namespace RhinoDB.Core.Tables { public class CustomTypeAttribute : System.Attribute { } }
        namespace MemoryPack { public class MemoryPackableAttribute : System.Attribute { } }
        namespace MessagePack { public class MessagePackObjectAttribute : System.Attribute { } }
        """;

    [Test]
    public void FlattenFields_NoCustomTypeFields_ReturnsFlatFieldList() {
        const string source = Prelude + """
            namespace TestNs;
            public readonly partial record struct Widget(int Id, string Name);
            """;
        var type = CompilationHelper.GetType(source, "TestNs.Widget");
        var ctor = SchemaWalk.FindPrimaryConstructor(type)!;

        var fields = DescriptorBuilder.FlattenFields(ctor.Parameters);

        Assert.That(fields.Select(f => f.Path), Is.EqualTo(new[] { "Id", "Name" }));
        Assert.That(fields[0].Kind, Is.EqualTo(RowFieldKind.Unmanaged));
        Assert.That(fields[1].Kind, Is.EqualTo(RowFieldKind.String));
    }

    [Test]
    public void FlattenFields_OneLevelCustomType_ProducesDottedPaths() {
        const string source = Prelude + """
            namespace TestNs;

            [RhinoDB.Core.Tables.CustomType]
            [MemoryPack.MemoryPackable]
            [MessagePack.MessagePackObject]
            public readonly partial record struct Loadout(int WeaponId, int SkinId);

            [MemoryPack.MemoryPackable]
            [MessagePack.MessagePackObject]
            public readonly partial record struct Player(int Id, string Name, Loadout Loadout);
            """;
        var type = CompilationHelper.GetType(source, "TestNs.Player");
        var ctor = SchemaWalk.FindPrimaryConstructor(type)!;

        var fields = DescriptorBuilder.FlattenFields(ctor.Parameters);

        Assert.That(fields.Select(f => f.Path), Is.EqualTo(new[] { "Id", "Name", "Loadout.WeaponId", "Loadout.SkinId" }));
        Assert.That(fields.Single(f => f.Path == "Loadout.WeaponId").Kind, Is.EqualTo(RowFieldKind.Unmanaged));
    }

    [Test]
    public void FlattenFields_ThreeLevelsDeepCustomTypeNesting_RecursesFully() {
        const string source = Prelude + """
            namespace TestNs;

            [RhinoDB.Core.Tables.CustomType]
            [MemoryPack.MemoryPackable]
            [MessagePack.MessagePackObject]
            public readonly partial record struct Cosmetics(int HatId, string SkinName);

            [RhinoDB.Core.Tables.CustomType]
            [MemoryPack.MemoryPackable]
            [MessagePack.MessagePackObject]
            public readonly partial record struct Loadout(int WeaponId, Cosmetics Cosmetics);

            [MemoryPack.MemoryPackable]
            [MessagePack.MessagePackObject]
            public readonly partial record struct Player(int Id, Loadout Loadout);
            """;
        var type = CompilationHelper.GetType(source, "TestNs.Player");
        var ctor = SchemaWalk.FindPrimaryConstructor(type)!;

        var fields = DescriptorBuilder.FlattenFields(ctor.Parameters);

        Assert.That(fields.Select(f => f.Path), Is.EqualTo(new[] {
            "Id", "Loadout.WeaponId", "Loadout.Cosmetics.HatId", "Loadout.Cosmetics.SkinName",
        }));
        Assert.That(fields.Single(f => f.Path == "Loadout.Cosmetics.HatId").Kind, Is.EqualTo(RowFieldKind.Unmanaged));
        Assert.That(fields.Single(f => f.Path == "Loadout.Cosmetics.SkinName").Kind, Is.EqualTo(RowFieldKind.String));
    }

    [Test]
    public void FlattenFields_TwoSiblingFieldsOfTheSameCustomType_BothExpandIndependently() {
        // Not a cycle - two DISTINCT fields sharing one CustomType. Guards the same class of bug this
        // project's UnmanagedTypeResolver cycle-guard fix caught earlier this session: a naive
        // "visited" set keyed only by type (not by field path) could wrongly treat the second sibling
        // as already-expanded and skip it.
        const string source = Prelude + """
            namespace TestNs;

            [RhinoDB.Core.Tables.CustomType]
            [MemoryPack.MemoryPackable]
            [MessagePack.MessagePackObject]
            public readonly partial record struct Point(int X, int Y);

            [MemoryPack.MemoryPackable]
            [MessagePack.MessagePackObject]
            public readonly partial record struct Line(Point Start, Point End);
            """;
        var type = CompilationHelper.GetType(source, "TestNs.Line");
        var ctor = SchemaWalk.FindPrimaryConstructor(type)!;

        var fields = DescriptorBuilder.FlattenFields(ctor.Parameters);

        Assert.That(fields.Select(f => f.Path), Is.EqualTo(new[] { "Start.X", "Start.Y", "End.X", "End.Y" }));
    }

    [Test]
    public void BuildTable_PopulatesEveryDescriptorField() {
        const string source = Prelude + """
            namespace TestNs;

            [MemoryPack.MemoryPackable]
            [MessagePack.MessagePackObject]
            public readonly partial record struct Club(int Id, int Rating);
            """;
        var type = CompilationHelper.GetType(source, "TestNs.Club");
        var ctor = SchemaWalk.FindPrimaryConstructor(type)!;

        var table = DescriptorBuilder.BuildTable(
            databaseFullName: "TestNs.GameDb",
            accessor: "Club",
            rowType: type,
            primaryKeyParam: ctor.Parameters[0],
            primaryCtorParams: ctor.Parameters,
            kind: "Persistent",
            tableIdHash: 999u,
            revision: 3
        );

        Assert.That(table.DatabaseFullName, Is.EqualTo("TestNs.GameDb"));
        Assert.That(table.Accessor, Is.EqualTo("Club"));
        Assert.That(table.RowTypeFullName, Is.EqualTo("global::TestNs.Club"));
        Assert.That(table.Kind, Is.EqualTo("Persistent"));
        Assert.That(table.TableIdHash, Is.EqualTo(999u));
        Assert.That(table.Revision, Is.EqualTo(3));
        Assert.That(table.PrimaryKey.Path, Is.EqualTo("Id"));
        Assert.That(table.Fields.Select(f => f.Path), Is.EqualTo(new[] { "Id", "Rating" }));
    }
}
