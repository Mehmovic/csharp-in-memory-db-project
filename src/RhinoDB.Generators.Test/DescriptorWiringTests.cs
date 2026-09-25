using System.Linq;

namespace RhinoDB.Generators.Test;

// AdditionalTextsProvider wiring for Descriptor.json (Phase 2, step 13) + RHINO019/020/023 (step 14).
public class DescriptorWiringTests {
    [Test]
    public void NoDescriptorProvided_GBinaryDefaultsToZero() {
        const string source = """
            using MemoryPack;
            using MessagePack;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class GameDb : DbContext<GameDbTransaction> { }

            [Table(TableKind.Instant, typeof(GameDb))]
            [MemoryPackable]
            [MessagePackObject]
            public readonly partial record struct Widget([PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id);
            """;

        var (asm, _) = GeneratorTestHost.CompileAndLoad(source);
        var gBinary = asm.GetType("TestNs.GameDb")!.GetField("G_binary")!.GetValue(null);

        Assert.That(gBinary, Is.EqualTo(0));
    }

    [Test]
    public void DescriptorProvided_GBinaryReflectsTheDatabasesCommittedGeneration() {
        const string descriptorJson = """
            {
              "Databases": [ { "FullName": "global::TestNs.GameDb", "Generation": 5, "InvalidGenerations": [], "RetainedFromGeneration": 0 } ],
              "TypeRevisions": {},
              "CustomTypes": {},
              "Tables": []
            }
            """;
        const string source = """
            using MemoryPack;
            using MessagePack;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class GameDb : DbContext<GameDbTransaction> { }

            [Table(TableKind.Instant, typeof(GameDb))]
            [MemoryPackable]
            [MessagePackObject]
            public readonly partial record struct Widget([PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id);
            """;

        var (asm, _) = GeneratorTestHost.CompileAndLoadWithDescriptor(source, descriptorJson);
        var gBinary = asm.GetType("TestNs.GameDb")!.GetField("G_binary")!.GetValue(null);

        Assert.That(gBinary, Is.EqualTo(5));
    }

    [Test]
    public void MalformedDescriptor_ReportsRHINO023AsAWarning_AndFallsBackToNoDescriptor() {
        const string source = """
            using MemoryPack;
            using MessagePack;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class GameDb : DbContext<GameDbTransaction> { }

            [Table(TableKind.Instant, typeof(GameDb))]
            [MemoryPackable]
            [MessagePackObject]
            public readonly partial record struct Widget([PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id);
            """;

        var (asm, diagnostics) = GeneratorTestHost.CompileAndLoadWithDescriptor(source, "not valid json {");

        Assert.That(diagnostics.Any(d => d.Id == "RHINO023"), Is.True);
        Assert.That(diagnostics.First(d => d.Id == "RHINO023").Severity, Is.EqualTo(Microsoft.CodeAnalysis.DiagnosticSeverity.Warning));
        Assert.That(asm.GetType("TestNs.GameDb")!.GetField("G_binary")!.GetValue(null), Is.EqualTo(0));
    }

    const string OldAccountDescriptor = """
        {
          "Databases": [],
          "TypeRevisions": {},
          "CustomTypes": {},
          "Tables": [
            {
              "DatabaseFullName": "global::TestNs.VaultDb",
              "Accessor": "Account",
              "RowTypeFullName": "global::TestNs.Account",
              "Kind": "Persistent",
              "TableIdHash": 0,
              "Revision": 0,
              "PrimaryKey": { "Path": "Id", "TypeFullName": "int", "Kind": "Unmanaged" },
              "Fields": [
                { "Path": "Id", "TypeFullName": "int", "Kind": "Unmanaged" },
                { "Path": "Balance", "TypeFullName": "decimal", "Kind": "Unmanaged" }
              ],
              "Indexes": []
            }
          ]
        }
        """;

    [Test]
    public void BreakingChangeAgainstCommittedDescriptor_NoMatchingMigration_ReportsRHINO019() {
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
                [property: MemoryPackOrder(1)] [property: Key(1)] long Balance);
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoadWithDescriptor(source, OldAccountDescriptor));
        Assert.That(ex!.Message, Does.Contain("RHINO019"));
    }

    [Test]
    public void BreakingChangeAgainstCommittedDescriptor_WithMatchingMigration_DoesNotThrow() {
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
                [property: MemoryPackOrder(1)] [property: Key(1)] long Balance) {
                [Migration(0)]
                internal static Account UpgradeFromV0(OldAccountShape old) => new Account(old.Id, (long)old.Balance);
            }
            """;

        Assert.DoesNotThrow(() => GeneratorTestHost.CompileAndLoadWithDescriptor(source, OldAccountDescriptor));
    }

    [Test]
    public void PureTrailingAppendAgainstCommittedDescriptor_IsAdditiveOnly_NeverRequiresAMigration() {
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
                [property: MemoryPackOrder(1)] [property: Key(1)] decimal Balance,
                [property: MemoryPackOrder(2)] [property: Key(2)] int LoyaltyPoints);
            """;

        Assert.DoesNotThrow(() => GeneratorTestHost.CompileAndLoadWithDescriptor(source, OldAccountDescriptor));
    }

    [Test]
    public void TableNotInCommittedDescriptor_IsANewTable_NeverRequiresAMigration() {
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
            public readonly partial record struct BrandNewTable(
                [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
                [property: MemoryPackOrder(1)] [property: Key(1)] string Name);
            """;

        Assert.DoesNotThrow(() => GeneratorTestHost.CompileAndLoadWithDescriptor(source, OldAccountDescriptor));
    }

    [Test]
    public void MigrationChainWithAnInternalGap_ReportsRHINO020() {
        const string source = """
            using MemoryPack;
            using MessagePack;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class VaultDb : DbContext<VaultDbTransaction> { }

            public readonly record struct AccountV0(int Id);
            public readonly record struct AccountV2(int Id, decimal Balance);

            [Table(TableKind.Persistent, typeof(VaultDb))]
            [MemoryPackable(GenerateType.VersionTolerant)]
            [MessagePackObject]
            public readonly partial record struct Account(
                [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
                [property: MemoryPackOrder(1)] [property: Key(1)] decimal Balance) {
                [Migration(0)]
                internal static AccountV2 UpgradeFromV0(AccountV0 old) => new AccountV2(old.Id, 0m);
                [Migration(2)]
                internal static Account UpgradeFromV2(AccountV2 old) => new Account(old.Id, old.Balance);
            }
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO020"));
    }
}
