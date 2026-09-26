using RhinoDB.Core;
using RhinoDB.Lib.Cold;
using RhinoDB.Lib.Durability;
using RhinoDB.Lib.Tables;
using RhinoDB.SchemaContracts;

namespace RhinoDB.Generators.Test;

// Phase 6, step 28: proves the generated {Db}.ConsolidateArchive() really rewrites an archived segment's
// bytes forward, not just advances a watermark. Mirrors GenesisReplayGenerationAwareTests.cs's archive
// seeding technique (an OLD [FrozenSchema(0)] shape's own Raw serializer, written directly into the
// archive) - after ConsolidateArchive() runs, the same segment must decode as the CURRENT shape without
// ever going through [Migration(0)] again, proving the bytes on disk actually changed.
public class ConsolidateArchiveEndToEndTests {
    const string Descriptor = """
        {
          "Databases": [ { "FullName": "global::TestNs.VaultDb", "Generation": 1, "InvalidGenerations": [], "RetainedFromGeneration": 0 } ],
          "TypeRevisions": {},
          "CustomTypes": {},
          "Tables": []
        }
        """;

    const string Source = """
        using MemoryPack;
        using MessagePack;
        using RhinoDB.Core.Tables;
        using RhinoDB.Lib.Execution;

        namespace TestNs;

        [Database]
        public partial class VaultDb : DbContext<VaultDbTransaction> { }

        [FrozenSchema(0)]
        public readonly record struct AccountV0([PrimaryKey] int Id, decimal Balance);

        [Table(TableKind.Persistent, typeof(VaultDb))]
        [MemoryPackable(GenerateType.VersionTolerant)]
        [MessagePackObject]
        public readonly partial record struct Account(
            [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
            [property: MemoryPackOrder(1)] [property: Key(1)] decimal Balance,
            [property: MemoryPackOrder(2)] [property: Key(2)] string Tier) {
            [Migration(0)]
            internal static Account UpgradeFromV0(AccountV0 old) => new Account(old.Id, old.Balance, "Bronze");
        }
        """;

    [Test]
    public async Task ConsolidateArchive_AnOldGenerationSegment_IsRewrittenToTheCurrentGenerationAndShape() {
        var dir = Path.Combine(Path.GetTempPath(), "rhinodb-consolidate-archive-e2e-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try {
            var (asm, _) = GeneratorTestHost.CompileAndLoadWithDescriptor(Source, Descriptor);
            var dbType = asm.GetType("TestNs.VaultDb")!;

            var oldRow = Activator.CreateInstance(asm.GetType("TestNs.AccountV0")!, 7, 150m)!;
            var oldKeyBytes = (byte[])GeneratorTestHost.InvokePrivateStaticHelper(asm, "TestNs.AccountV0FrozenSchemaOps", "SerializeKey", 7)!;
            var oldRowBytes = (byte[])GeneratorTestHost.InvokePrivateStaticHelper(asm, "TestNs.AccountV0FrozenSchemaOps", "SerializeRow", oldRow)!;

            var archiveDir = Path.Combine(dir, WalArchive.ArchiveDirectoryName);
            var change = new WalChange(TableIdHash.Compute("Account"), ChangeKind.Insert, oldKeyBytes, oldRowBytes);
            var entry = new DecodedWalEntry(3, WalEntryKind.Operation, [change]);
            var writeError = WalArchive.WriteSegment(archiveDir, Guid.NewGuid(), [entry], generation: 0);
            Assert.That(writeError, Is.Null);

            using var cold = ColdStore.Open(dir).Unwrap();
            Assert.That(cold.RunMigration(1, []).IsOk(), Is.True, "bump G_db to 1 (matching G_binary) with no rows to migrate - only the archive is under test here.");

            var db = Activator.CreateInstance(dbType, cold)!;
            var consolidateResult = (Result)dbType.GetMethod("ConsolidateArchive")!.Invoke(db, null)!;
            Assert.That(consolidateResult.IsOk(), Is.True);

            Assert.That(cold.ReadRetainedFromGeneration().Unwrap(), Is.EqualTo(1),
                "the watermark must advance to the same target generation the segments were consolidated to.");

            var history = WalArchive.ReadHistory(dir, [], 1).Unwrap();
            Assert.That(history, Has.Count.EqualTo(1));
            Assert.That(history[0].Generation, Is.EqualTo(1), "the segment must now be tagged with the CURRENT generation, not the original 0.");
            Assert.That(history[0].Entry.Lsn, Is.EqualTo(3), "LSN must be preserved exactly.");
            Assert.That(history[0].Entry.Changes[0].Kind, Is.EqualTo(ChangeKind.Insert), "ChangeKind must be preserved exactly.");

            var migratedRow = GeneratorTestHost.InvokePrivateStaticHelper(asm, "TestNs.VaultDbAccountOps", "DeserializeRow", history[0].Entry.Changes[0].Row!)!;
            Assert.That(migratedRow.GetType().GetProperty("Id")!.GetValue(migratedRow), Is.EqualTo(7));
            Assert.That(migratedRow.GetType().GetProperty("Balance")!.GetValue(migratedRow), Is.EqualTo(150m));
            Assert.That(migratedRow.GetType().GetProperty("Tier")!.GetValue(migratedRow), Is.EqualTo("Bronze"),
                "the bytes on disk must have actually gone through [Migration(0)] during consolidation, not just been re-tagged.");
        } finally {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task ConsolidateArchive_ASegmentAlreadyAtTheCurrentGeneration_IsLeftUntouched() {
        var dir = Path.Combine(Path.GetTempPath(), "rhinodb-consolidate-archive-untouched-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try {
            var (asm, _) = GeneratorTestHost.CompileAndLoadWithDescriptor(Source, Descriptor);
            var dbType = asm.GetType("TestNs.VaultDb")!;

            var liveRow = Activator.CreateInstance(asm.GetType("TestNs.Account")!, 9, 10m, "Gold")!;
            var liveKeyBytes = (byte[])GeneratorTestHost.InvokePrivateStaticHelper(asm, "TestNs.VaultDbAccountOps", "SerializeKey", 9)!;
            var liveRowBytes = (byte[])GeneratorTestHost.InvokePrivateStaticHelper(asm, "TestNs.VaultDbAccountOps", "SerializeRow", liveRow)!;

            var archiveDir = Path.Combine(dir, WalArchive.ArchiveDirectoryName);
            var change = new WalChange(TableIdHash.Compute("Account"), ChangeKind.Insert, liveKeyBytes, liveRowBytes);
            var entry = new DecodedWalEntry(1, WalEntryKind.Operation, [change]);
            WalArchive.WriteSegment(archiveDir, Guid.NewGuid(), [entry], generation: 1);

            using var cold = ColdStore.Open(dir).Unwrap();
            Assert.That(cold.RunMigration(1, []).IsOk(), Is.True);

            var db = Activator.CreateInstance(dbType, cold)!;
            var consolidateResult = (Result)dbType.GetMethod("ConsolidateArchive")!.Invoke(db, null)!;

            Assert.That(consolidateResult.IsOk(), Is.True);
            var history = WalArchive.ReadHistory(dir, [], 1).Unwrap();
            Assert.That(history[0].Entry.Changes[0].Row, Is.EqualTo(liveRowBytes), "already at the target generation - must be left byte-for-byte untouched.");
        } finally {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
