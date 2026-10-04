using MemoryPack;

using RhinoDB.Core;
using RhinoDB.Lib.Cold;
using RhinoDB.Lib.Execution;
using RhinoDB.Lib.Tables;
using RhinoDB.SchemaContracts;

namespace RhinoDB.Generators.Test;

// End-to-end proof that the generated {Db}.RunMigration() (Phase 4, step 20) really migrates a table's
// on-disk bytes: seeds a row using the OLD ([FrozenSchema(0)]) shape's own Raw serializer directly into cold
// storage (bypassing the live Ops class entirely, since the live SerializeRow only knows the current
// shape), runs the real generated migration, then proves the migrated bytes are correct by loading them
// back through the ordinary {Db}Loader path and reading the row through the live accessor.
public class RunMigrationEndToEndTests {
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
    public async Task RunMigration_ARealBreakingChange_MigratesOldBytesAndTheyAreLoadableAfterward() {
        var dir = Path.Combine(Path.GetTempPath(), "rhinodb-migration-e2e-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try {
            var (asm, _) = GeneratorTestHost.CompileAndLoadWithDescriptor(Source, Descriptor);
            var dbType = asm.GetType("TestNs.VaultDb")!;
            var loaderType = asm.GetType("TestNs.VaultDbLoader")!;
            var txType = asm.GetType("TestNs.VaultDbTransaction")!;

            using (var seedCold = ColdStore.Open(dir).Unwrap()) {
                Activator.CreateInstance(dbType, seedCold);

                var oldRow = Activator.CreateInstance(asm.GetType("TestNs.AccountV0")!, 7, 150m)!;
                var oldKeyBytes = (byte[])GeneratorTestHost.InvokePrivateStaticHelper(asm, "TestNs.AccountV0FrozenSchemaOps", "SerializeKey", 7)!;
                var oldRowBytes = (byte[])GeneratorTestHost.InvokePrivateStaticHelper(asm, "TestNs.AccountV0FrozenSchemaOps", "SerializeRow", oldRow)!;

                seedCold.BeginScope();
                seedCold.Stage(TableIdHash.Compute("Account"), ChangeKind.Insert, oldKeyBytes, oldRowBytes);
                await seedCold.EndScope(commit: true, PropagationMode.Confirmed, lsn: 1);
            }

            using var cold = ColdStore.Open(dir).Unwrap();
            var db = Activator.CreateInstance(dbType, cold)!;
            cold.CompleteRecovery();

            var runMigrationResult = (Result)dbType.GetMethod("RunMigration")!.Invoke(db, null)!;
            Assert.That(runMigrationResult.IsOk(), Is.True);
            Assert.That(cold.ReadGeneration().Unwrap(), Is.EqualTo(1));

            var currentGenerationResult = (Result<int>)dbType.GetMethod("CurrentGeneration")!.Invoke(db, null)!;
            Assert.That(currentGenerationResult.Unwrap(), Is.EqualTo(1),
                "the public db.CurrentGeneration() wrapper must agree with cold.ReadGeneration() - it's a thin passthrough.");

            var loader = Activator.CreateInstance(loaderType)!;
            await (Task)loaderType.GetMethod("LoadAsync")!.Invoke(loader, [db])!;

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

            Assert.That(found, Is.True);
            Assert.That(balance, Is.EqualTo(150m));
            Assert.That(tier, Is.EqualTo("Bronze"));
        } finally {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    private const string DescriptorWithOrphan = """
                                                {
                                                  "Databases": [ { "FullName": "global::TestNs.VaultDb", "Generation": 1, "InvalidGenerations": [], "RetainedFromGeneration": 0 } ],
                                                  "TypeRevisions": {},
                                                  "CustomTypes": {},
                                                  "Tables": [
                                                    {
                                                      "DatabaseFullName": "global::TestNs.VaultDb",
                                                      "Accessor": "Legacy",
                                                      "RowTypeFullName": "global::TestNs.Legacy",
                                                      "Kind": "Persistent",
                                                      "TableIdHash": 0,
                                                      "Revision": 0,
                                                      "PrimaryKey": { "Path": "Id", "TypeFullName": "int", "Kind": "Unmanaged" },
                                                      "Fields": [ { "Path": "Id", "TypeFullName": "int", "Kind": "Unmanaged" } ],
                                                      "Indexes": [],
                                                      "RemovedAtGeneration": 0
                                                    }
                                                  ]
                                                }
                                                """;

    private const string SourceWithOnlyAccountLive = """
                                                     using MemoryPack;
                                                     using MessagePack;
                                                     using RhinoDB.Core.Tables;
                                                     using RhinoDB.Lib.Execution;

                                                     namespace TestNs;

                                                     [Database]
                                                     public partial class VaultDb : DbContext<VaultDbTransaction> { }

                                                     [Table<VaultDb>(TableKind.Persistent)]
                                                     [MemoryPackable]
                                                     [MessagePackObject]
                                                     public readonly partial record struct Account([PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id);
                                                     """;

    static private ColdTable<int, int> OpenLegacyTable(ColdStore cold) =>
        cold.OpenTable<int, int>("Legacy", k => MemoryPackSerializer.Serialize(k), b => MemoryPackSerializer.Deserialize<int>(b), b => MemoryPackSerializer.Deserialize<int>(b));

    [Test]
    public async Task RunMigration_AnOrphanedTable_IsDroppedOncePastItsRetentionGeneration() {
        var dir = Path.Combine(Path.GetTempPath(), "rhinodb-migration-orphan-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try {
            var (asm, _) = GeneratorTestHost.CompileAndLoadWithDescriptor(SourceWithOnlyAccountLive, DescriptorWithOrphan);
            var dbType = asm.GetType("TestNs.VaultDb")!;

            using (var seedCold = ColdStore.Open(dir).Unwrap()) {
                Activator.CreateInstance(dbType, seedCold);
                var legacy = OpenLegacyTable(seedCold);

                // Simulates an EARLIER migration that already bumped this database to generation 1 - the
                // orphan's RemovedAtGeneration(0) must be strictly LESS than the current generation for the
                // drop condition to trigger (the "kept one generation, then dropped" grace period), so the
                // very first migration ever run against a freshly-removed table must NOT drop it yet.
                Assert.That(seedCold.RunMigration(1, []).IsOk(), Is.True);

                seedCold.BeginScope();
                seedCold.Stage(TableIdHash.Compute("Legacy"), ChangeKind.Insert, MemoryPackSerializer.Serialize(1), MemoryPackSerializer.Serialize(42));
                await seedCold.EndScope(commit: true, PropagationMode.Confirmed, lsn: 1);
                Assert.That(seedCold.ScanAll(legacy).ToList(), Has.Count.EqualTo(0), "not yet committed to mdbx within this same session (no live checkpointing).");
            }

            using var cold = ColdStore.Open(dir).Unwrap();
            var db = Activator.CreateInstance(dbType, cold)!;
            var legacyBeforeMigration = OpenLegacyTable(cold);
            Assert.That(cold.CompleteRecovery().IsOk(), Is.True);
            Assert.That(cold.ScanAll(legacyBeforeMigration).ToList(), Has.Count.EqualTo(1), "sanity: the orphan row is really there before migration.");

            var result = (Result)dbType.GetMethod("RunMigration")!.Invoke(db, null)!;

            Assert.That(result.IsOk(), Is.True);
            var legacyAfterMigration = OpenLegacyTable(cold);
            Assert.That(cold.ScanAll(legacyAfterMigration).ToList(), Is.Empty,
                "the orphan table must be dropped entirely once past its retention generation - reopening its name finds a fresh, empty table.");
        } finally {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
