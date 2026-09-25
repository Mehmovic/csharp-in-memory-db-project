using System.Reflection;

namespace RhinoDB.Generators.Test;

// [Migration(FromRevision=N)] discovery (Persistent-kind tables only, per point E of the schema-migration
// design - Instant tables have no persisted Raw data to transform, so their Ops class never emits a
// MigrateFromRevision{N} wrapper even if the row type happens to carry one, e.g. because the same row type
// is also used by a Persistent table on a different database).
public class MigrationMethodTests {
    [Test]
    public void ValidMigrationMethod_EmitsMigrateFromRevisionWrapperThatDelegatesToIt() {
        const string source = """
            using MemoryPack;
            using MessagePack;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class VaultDb : DbContext<VaultDbTransaction> { }

            [FrozenSchema(0)]
            public readonly record struct OldAccountShape([PrimaryKey] int Id, decimal Balance);

            [Table(TableKind.Persistent, typeof(VaultDb))]
            [MemoryPackable(GenerateType.VersionTolerant)]
            [MessagePackObject]
            public readonly partial record struct Account(
                [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
                [property: MemoryPackOrder(1)] [property: Key(1)] decimal Balance) {
                [Migration(0)]
                internal static Account UpgradeFromV0(OldAccountShape old) => new Account(old.Id, old.Balance * 2);
            }
            """;

        var (asm, _) = GeneratorTestHost.CompileAndLoad(source);
        var opsType = asm.GetType("TestNs.VaultDbAccountOps")!;
        var wrapper = opsType.GetMethod("MigrateFromRevision0", BindingFlags.Public | BindingFlags.Static);

        Assert.That(wrapper, Is.Not.Null);
        Assert.That(wrapper!.ReturnType, Is.EqualTo(asm.GetType("TestNs.Account")));
        Assert.That(wrapper.GetParameters()[0].ParameterType, Is.EqualTo(asm.GetType("TestNs.OldAccountShape")));

        var oldShapeType = asm.GetType("TestNs.OldAccountShape")!;
        var oldRow = Activator.CreateInstance(oldShapeType, 7, 10m)!;
        var migrated = wrapper.Invoke(null, [oldRow])!;

        Assert.That(migrated.GetType().GetProperty("Id")!.GetValue(migrated), Is.EqualTo(7));
        Assert.That(migrated.GetType().GetProperty("Balance")!.GetValue(migrated), Is.EqualTo(20m));
    }

    [Test]
    public void TwoRegisteredMigrationMethods_EmitTwoDistinctWrappers() {
        const string source = """
            using MemoryPack;
            using MessagePack;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class VaultDb : DbContext<VaultDbTransaction> { }

            [FrozenSchema(0)]
            public readonly record struct AccountV0([PrimaryKey] int Id);
            [FrozenSchema(1)]
            public readonly record struct AccountV1([PrimaryKey] int Id, decimal Balance);

            [Table(TableKind.Persistent, typeof(VaultDb))]
            [MemoryPackable(GenerateType.VersionTolerant)]
            [MessagePackObject]
            public readonly partial record struct Account(
                [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
                [property: MemoryPackOrder(1)] [property: Key(1)] decimal Balance) {
                [Migration(0)]
                internal static AccountV1 UpgradeFromV0(AccountV0 old) => new AccountV1(old.Id, 0m);
                [Migration(1)]
                internal static Account UpgradeFromV1(AccountV1 old) => new Account(old.Id, old.Balance);
            }
            """;

        var (asm, _) = GeneratorTestHost.CompileAndLoad(source);
        var opsType = asm.GetType("TestNs.VaultDbAccountOps")!;

        Assert.That(opsType.GetMethod("MigrateFromRevision0", BindingFlags.Public | BindingFlags.Static), Is.Not.Null);
        Assert.That(opsType.GetMethod("MigrateFromRevision1", BindingFlags.Public | BindingFlags.Static), Is.Not.Null);
    }

    [Test]
    public void MigrationMethodOnInstantKindTable_NoWrapperEmitted_NoDiagnosticEither() {
        const string source = """
            using MemoryPack;
            using MessagePack;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class VaultDb : DbContext<VaultDbTransaction> { }

            public readonly record struct WidgetV0(int Id);

            [Table(TableKind.Instant, typeof(VaultDb))]
            [MemoryPackable]
            [MessagePackObject]
            public readonly partial record struct Widget([PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id) {
                [Migration(0)]
                internal static Widget UpgradeFromV0(WidgetV0 old) => new Widget(old.Id);
            }
            """;

        var (asm, _) = GeneratorTestHost.CompileAndLoad(source);
        var opsType = asm.GetType("TestNs.VaultDbWidgetOps")!;

        Assert.That(opsType.GetMethod("MigrateFromRevision0", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static), Is.Null);
    }

    [Test]
    public void MigrationMethodIsInstance_ReportsRHINO018() {
        const string source = """
            using MemoryPack;
            using MessagePack;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class VaultDb : DbContext<VaultDbTransaction> { }

            public readonly record struct AccountV0(int Id);

            [Table(TableKind.Persistent, typeof(VaultDb))]
            [MemoryPackable(GenerateType.VersionTolerant)]
            [MessagePackObject]
            public readonly partial record struct Account(
                [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
                [property: MemoryPackOrder(1)] [property: Key(1)] decimal Balance) {
                [Migration(0)]
                internal Account UpgradeFromV0(AccountV0 old) => new Account(old.Id, 0m);
            }
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO018"));
    }

    [Test]
    public void MigrationMethodIsPrivate_ReportsRHINO018() {
        const string source = """
            using MemoryPack;
            using MessagePack;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class VaultDb : DbContext<VaultDbTransaction> { }

            public readonly record struct AccountV0(int Id);

            [Table(TableKind.Persistent, typeof(VaultDb))]
            [MemoryPackable(GenerateType.VersionTolerant)]
            [MessagePackObject]
            public readonly partial record struct Account(
                [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
                [property: MemoryPackOrder(1)] [property: Key(1)] decimal Balance) {
                [Migration(0)]
                static Account UpgradeFromV0(AccountV0 old) => new Account(old.Id, 0m);
            }
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO018"));
    }

    [Test]
    public void MigrationMethodHasWrongParameterCount_ReportsRHINO018() {
        const string source = """
            using MemoryPack;
            using MessagePack;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class VaultDb : DbContext<VaultDbTransaction> { }

            [Table(TableKind.Persistent, typeof(VaultDb))]
            [MemoryPackable(GenerateType.VersionTolerant)]
            [MessagePackObject]
            public readonly partial record struct Account(
                [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
                [property: MemoryPackOrder(1)] [property: Key(1)] decimal Balance) {
                [Migration(0)]
                internal static Account UpgradeFromV0() => new Account(0, 0m);
            }
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO018"));
    }

    [Test]
    public void MigrationMethodReturnsVoid_ReportsRHINO018() {
        const string source = """
            using MemoryPack;
            using MessagePack;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class VaultDb : DbContext<VaultDbTransaction> { }

            public readonly record struct AccountV0(int Id);

            [Table(TableKind.Persistent, typeof(VaultDb))]
            [MemoryPackable(GenerateType.VersionTolerant)]
            [MessagePackObject]
            public readonly partial record struct Account(
                [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
                [property: MemoryPackOrder(1)] [property: Key(1)] decimal Balance) {
                [Migration(0)]
                internal static void UpgradeFromV0(AccountV0 old) { }
            }
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO018"));
    }
}
