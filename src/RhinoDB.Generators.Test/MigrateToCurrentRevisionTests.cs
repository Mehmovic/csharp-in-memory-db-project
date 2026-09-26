using System.Reflection;

namespace RhinoDB.Generators.Test;

// MigrateToCurrentRevision(int fromRevision, byte[] rowBytes) on a persistent table's Ops class (Phase 4,
// step 20's prerequisite) - the bridge between "which revision is this table's on-disk data actually at"
// (RevisionAtGeneration) and the live row type, walking the registered [Migration(FromRevision=N)] chain
// forward from whatever revision the bytes were captured at.
public class MigrateToCurrentRevisionTests {
    private const string TwoHopSource = """
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
                                            [property: MemoryPackOrder(1)] [property: Key(1)] decimal Balance,
                                            [property: MemoryPackOrder(2)] [property: Key(2)] string Tier) {
                                            [Migration(0)]
                                            internal static AccountV1 UpgradeFromV0(AccountV0 old) => new AccountV1(old.Id, 0m);
                                            [Migration(1)]
                                            internal static Account UpgradeFromV1(AccountV1 old) => new Account(old.Id, old.Balance, "Bronze");
                                        }
                                        """;

    static private object InvokeMigrateToCurrentRevision(System.Reflection.Assembly asm, int fromRevision, byte[] rowBytes) {
        var opsType = asm.GetType("TestNs.VaultDbAccountOps")!;
        var method = opsType.GetMethod("MigrateToCurrentRevision", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        return method.Invoke(null, [fromRevision, rowBytes])!;
    }

    [Test]
    public void StartingAtTheEarliestHop_AppliesBothHopsInSequence() {
        var (asm, _) = GeneratorTestHost.CompileAndLoad(TwoHopSource);
        var oldRow = Activator.CreateInstance(asm.GetType("TestNs.AccountV0")!, 7)!;
        var rowBytes = (byte[])GeneratorTestHost.InvokePrivateStaticHelper(asm, "TestNs.AccountV0FrozenSchemaOps", "SerializeRow", oldRow)!;

        var migrated = InvokeMigrateToCurrentRevision(asm, 0, rowBytes);

        Assert.That(migrated.GetType().GetProperty("Id")!.GetValue(migrated), Is.EqualTo(7));
        Assert.That(migrated.GetType().GetProperty("Balance")!.GetValue(migrated), Is.EqualTo(0m));
        Assert.That(migrated.GetType().GetProperty("Tier")!.GetValue(migrated), Is.EqualTo("Bronze"));
    }

    [Test]
    public void StartingMidChain_AppliesOnlyTheRemainingHop() {
        var (asm, _) = GeneratorTestHost.CompileAndLoad(TwoHopSource);
        var midRow = Activator.CreateInstance(asm.GetType("TestNs.AccountV1")!, 9, 55m)!;
        var rowBytes = (byte[])GeneratorTestHost.InvokePrivateStaticHelper(asm, "TestNs.AccountV1FrozenSchemaOps", "SerializeRow", midRow)!;

        var migrated = InvokeMigrateToCurrentRevision(asm, 1, rowBytes);

        Assert.That(migrated.GetType().GetProperty("Id")!.GetValue(migrated), Is.EqualTo(9));
        Assert.That(migrated.GetType().GetProperty("Balance")!.GetValue(migrated), Is.EqualTo(55m));
        Assert.That(migrated.GetType().GetProperty("Tier")!.GetValue(migrated), Is.EqualTo("Bronze"));
    }

    [Test]
    public void StartingAtTheTip_NoTransformApplied_JustDecodesTheLiveShape() {
        var (asm, _) = GeneratorTestHost.CompileAndLoad(TwoHopSource);
        var liveRow = Activator.CreateInstance(asm.GetType("TestNs.Account")!, 3, 10m, "Gold")!;
        var rowBytes = (byte[])GeneratorTestHost.InvokePrivateStaticHelper(asm, "TestNs.VaultDbAccountOps", "SerializeRow", liveRow)!;

        var migrated = InvokeMigrateToCurrentRevision(asm, 2, rowBytes);

        Assert.That(migrated, Is.EqualTo(liveRow));
    }

    [Test]
    public void StartingPastTheTip_StillDecodesTheLiveShape() {
        var (asm, _) = GeneratorTestHost.CompileAndLoad(TwoHopSource);
        var liveRow = Activator.CreateInstance(asm.GetType("TestNs.Account")!, 3, 10m, "Gold")!;
        var rowBytes = (byte[])GeneratorTestHost.InvokePrivateStaticHelper(asm, "TestNs.VaultDbAccountOps", "SerializeRow", liveRow)!;

        var migrated = InvokeMigrateToCurrentRevision(asm, 99, rowBytes);

        Assert.That(migrated, Is.EqualTo(liveRow));
    }

    [Test]
    public void TableWithNoMigrationMethodsAtAll_AlwaysDecodesTheLiveShape() {
        const string source = """
            using MemoryPack;
            using MessagePack;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class VaultDb : DbContext<VaultDbTransaction> { }

            [Table(TableKind.Persistent, typeof(VaultDb))]
            [MemoryPackable]
            [MessagePackObject]
            public readonly partial record struct Widget([PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id);
            """;

        var (asm, _) = GeneratorTestHost.CompileAndLoad(source);
        var liveRow = Activator.CreateInstance(asm.GetType("TestNs.Widget")!, 42)!;
        var rowBytes = (byte[])GeneratorTestHost.InvokePrivateStaticHelper(asm, "TestNs.VaultDbWidgetOps", "SerializeRow", liveRow)!;

        var opsType = asm.GetType("TestNs.VaultDbWidgetOps")!;
        var method = opsType.GetMethod("MigrateToCurrentRevision", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        var migrated = method.Invoke(null, [0, rowBytes])!;

        Assert.That(migrated, Is.EqualTo(liveRow));
    }
}
