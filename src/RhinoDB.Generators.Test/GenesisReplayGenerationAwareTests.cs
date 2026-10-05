using RhinoDB.Core;
using RhinoDB.Lib.Cold;
using RhinoDB.Lib.Durability;
using RhinoDB.Lib.Execution;
using RhinoDB.Lib.Tables;
using RhinoDB.SchemaContracts;

namespace RhinoDB.Generators.Test;

// Phase 5, step 23: proves LoadFromGenesis is generation-aware, not just a passthrough to the live
// deserializer. Seeds an ARCHIVED WAL segment directly (WalArchive.WriteSegment) containing bytes
// produced by the OLD ([FrozenSchema(0)]) shape's own Raw serializer, tagged with the OLD generation -
// mirroring RunMigrationEndToEndTests.cs's cold-storage seeding technique, but targeting the archive
// instead, since that's what genesis replay actually reads from. A fresh ColdStore/db is then pointed at
// that directory and LoadFromGenesis is run with NO migration having ever executed - the row must still
// come out correctly migrated, purely from replaying history.
public class GenesisReplayGenerationAwareTests {
    private const string Descriptor = """
                                      {
                                        "Databases": [ { "FullName": "global::TestNs.VaultDb", "Generation": 1, "InvalidGenerations": [], "RetainedFromGeneration": 0 } ],
                                        "TypeRevisions": {},
                                        "CustomTypes": {},
                                        "Tables": []
                                      }
                                      """;

    private const string Source = """
                                  using MemoryPack;
                                  using MessagePack;
                                  using RhinoDB.Core.Tables;
                                  using RhinoDB.Lib.Execution;

                                  namespace TestNs;

                                  [Database]
                                  public partial class VaultDb : DbContext<VaultDbTransaction> { }

                                  [FrozenSchema(0)]
                                  public readonly record struct AccountV0([PrimaryKey] int Id, decimal Balance);

                                  [Table<VaultDb>(TableKind.Persistent)]
                                  [MemoryPackable(GenerateType.VersionTolerant)]
                                  [MessagePackObject]
                                  public readonly partial record struct Account(
                                      [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
                                      [property: MemoryPackOrder(1)] [property: Key(1)] decimal Balance,
                                      [property: MemoryPackOrder(2)] [property: Key(2)] string Tier) {
                                      [Migration(0)]
                                      internal static Account UpgradeFromV0(AccountV0 old) => new Account(old.Id, old.Balance, "Bronze");
                                  }

                                  public static class TestHelpers {
                                      public static bool AccountFound(VaultDbAccountOps ops, int id) => ops.Primary.Find(id).HasRow();
                                      public static decimal AccountBalance(VaultDbAccountOps ops, int id) => ops.Primary.Find(id).Get().Unwrap().Balance;
                                      public static string AccountTier(VaultDbAccountOps ops, int id) => ops.Primary.Find(id).Get().Unwrap().Tier;
                                  }
                                  """;

    [Test]
    public async Task LoadFromGenesis_AnArchivedSegmentTaggedWithAnOldGeneration_MigratesItBeforeApplying() {
        var dir = Path.Combine(Path.GetTempPath(), "rhinodb-genesis-generation-aware-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try {
            var (asm, _) = GeneratorTestHost.CompileAndLoadWithDescriptor(Source, Descriptor);
            var dbType = asm.GetType("TestNs.VaultDb")!;
            var loaderType = asm.GetType("TestNs.VaultDbLoader")!;
            var txType = asm.GetType("TestNs.VaultDbTransaction")!;

            var oldRow = Activator.CreateInstance(asm.GetType("TestNs.AccountV0")!, 7, 150m)!;
            var oldKeyBytes = (byte[])GeneratorTestHost.InvokePrivateStaticHelper(asm, "TestNs.AccountV0FrozenSchemaOps", "SerializeKey", 7)!;
            var oldRowBytes = (byte[])GeneratorTestHost.InvokePrivateStaticHelper(asm, "TestNs.AccountV0FrozenSchemaOps", "SerializeRow", oldRow)!;

            var archiveDir = Path.Combine(dir, WalArchive.ArchiveDirectoryName);
            var change = new WalChange(NameHash.Compute("Account"), ChangeKind.Insert, oldKeyBytes, oldRowBytes);
            var entry = new DecodedWalEntry(0, WalEntryKind.Operation, [change]);
            var writeError = WalArchive.WriteSegment(archiveDir, Guid.NewGuid(), [entry], generation: 0);
            Assert.That(writeError, Is.Null, "seeding the archived segment itself must succeed.");

            using var cold = ColdStore.Open(dir).Unwrap();
            var db = Activator.CreateInstance(dbType, cold)!;
            var loader = Activator.CreateInstance(loaderType)!;

            var replayResult = (Result)loaderType.GetMethod("LoadFromGenesis")!.Invoke(loader, [db, cold, null, null])!;
            Assert.That(replayResult.IsOk(), Is.True, "genesis replay must succeed even though no migration ever ran.");

            bool found = false;
            var balance = 0m;
            var tier = "";
            await (Task<Result>)GeneratorTestHost.RunTransactional(
                db, txType, (ctx, tx) => {
                    dynamic dtx = tx;
                    found = (bool)GeneratorTestHost.InvokeHelper(asm, "TestNs.TestHelpers", "AccountFound", (object)dtx.Account, 7)!;
                    balance = (decimal)GeneratorTestHost.InvokeHelper(asm, "TestNs.TestHelpers", "AccountBalance", (object)dtx.Account, 7)!;
                    tier = (string)GeneratorTestHost.InvokeHelper(asm, "TestNs.TestHelpers", "AccountTier", (object)dtx.Account, 7)!;
                    return Result.Ok();
                }, PropagationMode.Optimistic);

            Assert.That(found, Is.True, "the row from the old-generation archived segment must be present after replay.");
            Assert.That(balance, Is.EqualTo(150m));
            Assert.That(tier, Is.EqualTo("Bronze"), "the row must have gone through [Migration(0)] during replay, not been misread as current-shape bytes.");
        } finally {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    private const string DescriptorWithFloorAtOne = """
                                                    {
                                                      "Databases": [ { "FullName": "global::TestNs.VaultDb", "Generation": 1, "InvalidGenerations": [], "RetainedFromGeneration": 1 } ],
                                                      "TypeRevisions": {},
                                                      "CustomTypes": {},
                                                      "Tables": []
                                                    }
                                                    """;

    [Test]
    public void LoadFromGenesis_AnArchivedSegmentOlderThanTheRetentionFloor_RefusesBeforeDecodingAnything() {
        var dir = Path.Combine(Path.GetTempPath(), "rhinodb-genesis-floor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try {
            // Same source/fixture as above, but this binary's own RetainedFromGeneration is 1 - simulating a
            // deployment that has already pruned/consolidated its archive past generation 0. A segment still
            // tagged generation 0 sitting on disk (e.g. restored from an old backup) must be refused outright,
            // not partially replayed with garbage.
            var (asm, _) = GeneratorTestHost.CompileAndLoadWithDescriptor(Source, DescriptorWithFloorAtOne);
            var dbType = asm.GetType("TestNs.VaultDb")!;
            var loaderType = asm.GetType("TestNs.VaultDbLoader")!;

            var oldRow = Activator.CreateInstance(asm.GetType("TestNs.AccountV0")!, 7, 150m)!;
            var oldKeyBytes = (byte[])GeneratorTestHost.InvokePrivateStaticHelper(asm, "TestNs.AccountV0FrozenSchemaOps", "SerializeKey", 7)!;
            var oldRowBytes = (byte[])GeneratorTestHost.InvokePrivateStaticHelper(asm, "TestNs.AccountV0FrozenSchemaOps", "SerializeRow", oldRow)!;

            var archiveDir = Path.Combine(dir, WalArchive.ArchiveDirectoryName);
            var change = new WalChange(NameHash.Compute("Account"), ChangeKind.Insert, oldKeyBytes, oldRowBytes);
            var entry = new DecodedWalEntry(0, WalEntryKind.Operation, [change]);
            WalArchive.WriteSegment(archiveDir, Guid.NewGuid(), [entry], generation: 0);

            using var cold = ColdStore.Open(dir).Unwrap();
            var db = Activator.CreateInstance(dbType, cold)!;
            var loader = Activator.CreateInstance(loaderType)!;

            var replayResult = (Result)loaderType.GetMethod("LoadFromGenesis")!.Invoke(loader, [db, cold, null, null])!;

            Assert.That(replayResult.IsError(), Is.True);
            Assert.That(replayResult.GetError().Kind, Is.EqualTo(ErrorKind.ArchiveOlderThanRetentionFloor));
        } finally {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
