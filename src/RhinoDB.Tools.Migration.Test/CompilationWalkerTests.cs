using RhinoDB.Tools.Migration;

namespace RhinoDB.Tools.Migration.Test;

public class CompilationWalkerTests {
    [Test]
    public void InstantTable_ProducesATableDescriptorWithCorrectKindAndFields() {
        var compilation = CompilationHelper.Compile("""
            using RhinoDB.Core.Tables;

            namespace TestNs;

            [Database]
            public partial class GameDb { }

            [Table(TableKind.Instant, typeof(GameDb))]
            public readonly partial record struct Widget([PrimaryKey] int Id, string Name);
            """);

        var descriptor = CompilationWalker.BuildDescriptor(compilation);

        Assert.That(descriptor.Tables, Has.Count.EqualTo(1));
        var table = descriptor.Tables[0];
        Assert.That(table.Accessor, Is.EqualTo("Widget"));
        Assert.That(table.Kind, Is.EqualTo("Instant"));
        Assert.That(table.PrimaryKey.Path, Is.EqualTo("Id"));
        Assert.That(table.Fields.Select(f => f.Path), Is.EqualTo(new[] { "Id", "Name" }));
    }

    [Test]
    public void PersistentTable_KindIsPersistent() {
        var compilation = CompilationHelper.Compile("""
            using RhinoDB.Core.Tables;

            namespace TestNs;

            [Database]
            public partial class GameDb { }

            [Table(TableKind.Persistent, typeof(GameDb))]
            public readonly partial record struct Account([PrimaryKey] int Id, decimal Balance);
            """);

        var descriptor = CompilationWalker.BuildDescriptor(compilation);

        Assert.That(descriptor.Tables.Single().Kind, Is.EqualTo("Persistent"));
    }

    [Test]
    public void ExplicitAccessorNamedArgument_OverridesTheTypeName() {
        var compilation = CompilationHelper.Compile("""
            using RhinoDB.Core.Tables;

            namespace TestNs;

            [Database]
            public partial class GameDb { }

            [Table(TableKind.Instant, typeof(GameDb), Accessor = "Players")]
            public readonly partial record struct Widget([PrimaryKey] int Id);
            """);

        var descriptor = CompilationWalker.BuildDescriptor(compilation);

        Assert.That(descriptor.Tables.Single().Accessor, Is.EqualTo("Players"));
    }

    [Test]
    public void RowTypeUsedByTwoDatabases_ProducesTwoTableDescriptors() {
        var compilation = CompilationHelper.Compile("""
            using RhinoDB.Core.Tables;

            namespace TestNs;

            [Database]
            public partial class GameDb { }

            [Database]
            public partial class AdminDb { }

            [Table(TableKind.Instant, typeof(GameDb))]
            [Table(TableKind.Instant, typeof(AdminDb))]
            public readonly partial record struct Widget([PrimaryKey] int Id);
            """);

        var descriptor = CompilationWalker.BuildDescriptor(compilation);

        Assert.That(descriptor.Tables, Has.Count.EqualTo(2));
        Assert.That(descriptor.Tables.Select(t => t.DatabaseFullName), Is.EquivalentTo(new[] { "global::TestNs.GameDb", "global::TestNs.AdminDb" }));
    }

    [Test]
    public void TableRowWithNoPrimaryKey_IsSkippedEntirely_DoesNotThrow() {
        var compilation = CompilationHelper.Compile("""
            using RhinoDB.Core.Tables;

            namespace TestNs;

            [Database]
            public partial class GameDb { }

            [Table(TableKind.Instant, typeof(GameDb))]
            public readonly partial record struct Widget(int Id);
            """);

        var descriptor = CompilationWalker.BuildDescriptor(compilation);

        Assert.That(descriptor.Tables, Is.Empty);
    }

    [Test]
    public void DatabaseType_AppearsInTheDatabasesList() {
        var compilation = CompilationHelper.Compile("""
            using RhinoDB.Core.Tables;

            namespace TestNs;

            [Database]
            public partial class GameDb { }
            """);

        var descriptor = CompilationWalker.BuildDescriptor(compilation);

        Assert.That(descriptor.Databases.Single().FullName, Is.EqualTo("global::TestNs.GameDb"));
    }

    [Test]
    public void EmbeddedCustomTypeField_IsFlattenedIntoDottedPaths() {
        var compilation = CompilationHelper.Compile("""
            using RhinoDB.Core.Tables;

            namespace TestNs;

            [Database]
            public partial class GameDb { }

            [CustomType]
            public readonly partial record struct Loadout(int WeaponId);

            [Table(TableKind.Instant, typeof(GameDb))]
            public readonly partial record struct Player([PrimaryKey] int Id, Loadout Loadout);
            """);

        var descriptor = CompilationWalker.BuildDescriptor(compilation);

        Assert.That(descriptor.Tables.Single().Fields.Select(f => f.Path), Is.EqualTo(new[] { "Id", "Loadout.WeaponId" }));
    }

    [Test]
    public void PartialTypeDeclaredAcrossTwoFragmentsInOneCompilation_IsCountedOnce() {
        // Guards the real duplicate-table bug caught via a manual smoke test against
        // RhinoDB.Run.Server.Sandbox under MSBuildWorkspace - a partial type's declaration spanning more
        // than one syntax node must never be double-counted as two tables.
        var compilation = CompilationHelper.Compile("""
            using RhinoDB.Core.Tables;

            namespace TestNs;

            [Database]
            public partial class GameDb { }

            [Table(TableKind.Instant, typeof(GameDb))]
            public readonly partial record struct Widget([PrimaryKey] int Id);

            public readonly partial record struct Widget {
                public int ExtraComputedThing => Id;
            }
            """);

        var descriptor = CompilationWalker.BuildDescriptor(compilation);

        Assert.That(descriptor.Tables, Has.Count.EqualTo(1));
    }
}
