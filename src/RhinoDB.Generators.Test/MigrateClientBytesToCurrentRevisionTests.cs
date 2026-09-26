using System.Reflection;

using RhinoDB.SchemaContracts;

namespace RhinoDB.Generators.Test;

// MigrateClientBytesToCurrentRevision(int fromRevision, byte[] clientBytes) - CompatAdapter's upgrade
// direction (Part F, Phase 3). Reuses the exact same [Migration(FromRevision=N)] chain
// MigrateToCurrentRevision already composes, substituting the starting/tip decode calls for whichever
// client-format decoder the project's ClientProtocol resolves to - only emitted when that protocol isn't
// Raw (Raw callers reuse MigrateToCurrentRevision verbatim instead).
//
// Rows stay fully unmanaged throughout - MemoryPackSerializer.Serialize/Deserialize<T> has a formatter-free
// blit fast path for those (confirmed elsewhere, e.g. RawSerializerTests.cs), which is what makes a real
// round-trip provable in this harness at all (it only runs TableGenerator/CustomTypeGenerator/
// FrozenSchemaGenerator, never the real MemoryPack.Generator/MessagePackAnalyzer - see
// CompileAndLoadWithSerializationGenerators's own doc comment). MessagePack has no equivalent fast path, so
// the MessagePack-protocol test below only proves the method exists with the right signature, not a real
// invocation.
public class MigrateClientBytesToCurrentRevisionTests {
    private const string TwoHopSource = """
                                        using MemoryPack;
                                        using RhinoDB.Core.Tables;
                                        using RhinoDB.Lib.Execution;

                                        namespace TestNs;

                                        [Database]
                                        public partial class VaultDb : DbContext<VaultDbTransaction> { }

                                        [FrozenSchema(0)]
                                        [MemoryPackable]
                                        public readonly record struct AccountV0([PrimaryKey] [property: MemoryPackOrder(0)] int Id);

                                        [FrozenSchema(1)]
                                        [MemoryPackable]
                                        public readonly record struct AccountV1(
                                            [PrimaryKey] [property: MemoryPackOrder(0)] int Id,
                                            [property: MemoryPackOrder(1)] decimal Balance);

                                        [Table(TableKind.Persistent, typeof(VaultDb))]
                                        [MemoryPackable]
                                        public readonly partial record struct Account(
                                            [PrimaryKey] [property: MemoryPackOrder(0)] int Id,
                                            [property: MemoryPackOrder(1)] decimal Balance,
                                            [property: MemoryPackOrder(2)] int TierCode) {
                                            [Migration(0)]
                                            internal static AccountV1 UpgradeFromV0(AccountV0 old) => new AccountV1(old.Id, 0m);
                                            [Migration(1)]
                                            internal static Account UpgradeFromV1(AccountV1 old) => new Account(old.Id, old.Balance, 1);
                                        }
                                        """;

    static private object InvokeMigrateClientBytes(Assembly asm, int fromRevision, byte[] clientBytes) {
        var opsType = asm.GetType("TestNs.VaultDbAccountOps")!;
        var method = opsType.GetMethod("MigrateClientBytesToCurrentRevision", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        return method.Invoke(null, [fromRevision, clientBytes])!;
    }

    [Test]
    public void VersionedMemoryPackProtocol_StartingAtTheEarliestHop_AppliesBothHopsInSequence() {
        var (asm, _) = GeneratorTestHost.CompileAndLoadWithClientProtocol(TwoHopSource, ClientProtocolKind.VersionedMemoryPack);
        var oldRow = Activator.CreateInstance(asm.GetType("TestNs.AccountV0")!, 7)!;
        var clientBytes = (byte[])GeneratorTestHost.InvokePrivateStaticHelper(asm, "TestNs.AccountV0FrozenSchemaOps", "SerializeVersionedMemoryPack", oldRow)!;

        var migrated = InvokeMigrateClientBytes(asm, 0, clientBytes);

        Assert.That(migrated.GetType().GetProperty("Id")!.GetValue(migrated), Is.EqualTo(7));
        Assert.That(migrated.GetType().GetProperty("Balance")!.GetValue(migrated), Is.EqualTo(0m));
        Assert.That(migrated.GetType().GetProperty("TierCode")!.GetValue(migrated), Is.EqualTo(1));
    }

    [Test]
    public void VersionedMemoryPackProtocol_StartingMidChain_AppliesOnlyTheRemainingHop() {
        var (asm, _) = GeneratorTestHost.CompileAndLoadWithClientProtocol(TwoHopSource, ClientProtocolKind.VersionedMemoryPack);
        var midRow = Activator.CreateInstance(asm.GetType("TestNs.AccountV1")!, 9, 55m)!;
        var clientBytes = (byte[])GeneratorTestHost.InvokePrivateStaticHelper(asm, "TestNs.AccountV1FrozenSchemaOps", "SerializeVersionedMemoryPack", midRow)!;

        var migrated = InvokeMigrateClientBytes(asm, 1, clientBytes);

        Assert.That(migrated.GetType().GetProperty("Id")!.GetValue(migrated), Is.EqualTo(9));
        Assert.That(migrated.GetType().GetProperty("Balance")!.GetValue(migrated), Is.EqualTo(55m));
        Assert.That(migrated.GetType().GetProperty("TierCode")!.GetValue(migrated), Is.EqualTo(1));
    }

    [Test]
    public void VersionedMemoryPackProtocol_StartingAtTheTip_NoTransformApplied_JustDecodesTheLiveShape() {
        var (asm, _) = GeneratorTestHost.CompileAndLoadWithClientProtocol(TwoHopSource, ClientProtocolKind.VersionedMemoryPack);
        var liveRow = Activator.CreateInstance(asm.GetType("TestNs.Account")!, 3, 10m, 2)!;
        var clientBytes = (byte[])GeneratorTestHost.InvokePrivateStaticHelper(asm, "TestNs.VaultDbAccountOps", "SerializeVersionedMemoryPack", liveRow)!;

        var migrated = InvokeMigrateClientBytes(asm, 2, clientBytes);

        Assert.That(migrated, Is.EqualTo(liveRow));
    }

    [Test]
    public void VersionedMemoryPackProtocol_StartingPastTheTip_StillDecodesTheLiveShape() {
        var (asm, _) = GeneratorTestHost.CompileAndLoadWithClientProtocol(TwoHopSource, ClientProtocolKind.VersionedMemoryPack);
        var liveRow = Activator.CreateInstance(asm.GetType("TestNs.Account")!, 3, 10m, 2)!;
        var clientBytes = (byte[])GeneratorTestHost.InvokePrivateStaticHelper(asm, "TestNs.VaultDbAccountOps", "SerializeVersionedMemoryPack", liveRow)!;

        var migrated = InvokeMigrateClientBytes(asm, 99, clientBytes);

        Assert.That(migrated, Is.EqualTo(liveRow));
    }

    [Test]
    public void MessagePackProtocol_MethodExistsWithTheRightSignature() {
        const string source = """
            using MessagePack;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class VaultDb : DbContext<VaultDbTransaction> { }

            [FrozenSchema(0)]
            [MessagePackObject]
            public readonly record struct AccountV0([PrimaryKey] [property: Key(0)] int Id);

            [Table(TableKind.Persistent, typeof(VaultDb))]
            [MessagePackObject]
            public readonly partial record struct Account([PrimaryKey] [property: Key(0)] int Id, [property: Key(1)] decimal Balance) {
                [Migration(0)]
                internal static Account UpgradeFromV0(AccountV0 old) => new Account(old.Id, 0m);
            }
            """;

        var (asm, _) = GeneratorTestHost.CompileAndLoadWithClientProtocol(source, ClientProtocolKind.MessagePack);
        var opsType = asm.GetType("TestNs.VaultDbAccountOps")!;
        var method = opsType.GetMethod("MigrateClientBytesToCurrentRevision", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

        Assert.That(method, Is.Not.Null);
        Assert.That(method!.GetParameters().Select(p => p.ParameterType.Name), Is.EqualTo(new[] { "Int32", "Byte[]" }));
    }

    [Test]
    public void RawProtocol_MethodIsEntirelyAbsent() {
        // The [MemoryPackable] attributes already on TwoHopSource are harmless-but-unrequired under Raw
        // (RHINO015/025 simply don't check for them) - no need to strip them for this test.
        var (asm, _) = GeneratorTestHost.CompileAndLoad(TwoHopSource);
        var opsType = asm.GetType("TestNs.VaultDbAccountOps")!;

        Assert.That(opsType.GetMethod("MigrateClientBytesToCurrentRevision", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static), Is.Null);
    }
}
