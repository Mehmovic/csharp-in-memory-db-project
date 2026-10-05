using MemoryPack;

using RhinoDB.Lib.Durability;
using RhinoDB.Lib.Execution;
using RhinoDB.Lib.Tables;
using RhinoDB.SchemaContracts;

namespace RhinoDB.Lib.Cold.Test;

// ColdStore.RunMigration - the drop-recreate-copy mechanics behind Phase 4 step 20
// (Docs/06-schema-migration.md §3/§9's "ONE mdbx write txn: transform every persistent table").
// Deliberately tested here against a hand-typed fixture and a raw byte-transform delegate, before any
// real [Migration]/[FrozenSchema] machinery is wired in - proving the table-agnostic mechanics (scratch
// dbi, drop, reopen, copy-back, atomic generation write, cached ColdTable dbi-handle rotation) work
// correctly in isolation, per the plan's own "mechanics before real frozen history exists" ordering.
public class MigrationTransactionTests {
    private string dir = "";

    [SetUp]
    public void SetUp() {
        dir = Path.Combine(Path.GetTempPath(), "rhinodb-migration-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
    }

    [TearDown]
    public void TearDown() {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    static private void StageInsert(ColdStore store, string tableName, int key, Account row) =>
        store.Stage(NameHash.Compute(tableName), ChangeKind.Insert, MemoryPackSerializer.Serialize(key), MemoryPackSerializer.Serialize(row));

    static private async Task RunConfirmed(ColdStore store, Action stageActions, ulong lsn) {
        store.BeginScope();
        stageActions();
        await store.EndScope(commit: true, PropagationMode.Confirmed, lsn);
    }

    static private (byte[] Key, byte[] Row) DoubleBalance(byte[] keyBytes, byte[] rowBytes) {
        var account = MemoryPackSerializer.Deserialize<Account>(rowBytes);
        var doubled = new Account(account.Id, account.Owner, account.Balance * 2);
        return (keyBytes, MemoryPackSerializer.Serialize(doubled));
    }

    [Test]
    public async Task RunMigration_TransformsEveryRowAndKeepsThemReadableUnderTheSameTableName() {
        using (var seed = ColdStore.Open(dir).Unwrap()) {
            seed.OpenTable<int, Account>("accounts");
            await RunConfirmed(seed, () => {
                StageInsert(seed, "accounts", 1, new Account(1, "alice@example.com", 100m));
                StageInsert(seed, "accounts", 2, new Account(2, "bob@example.com", 50m));
            }, lsn: 1);
        }

        using var store = ColdStore.Open(dir).Unwrap();
        var accounts = store.OpenTable<int, Account>("accounts");
        store.CompleteRecovery();

        var result = store.RunMigration(targetGeneration: 1, [("accounts", DoubleBalance)]);

        Assert.That(result.IsOk(), Is.True);
        Assert.That(store.Peek(accounts, 1).Unwrap().Balance, Is.EqualTo(200m));
        Assert.That(store.Peek(accounts, 2).Unwrap().Balance, Is.EqualTo(100m));
    }

    [Test]
    public async Task RunMigration_UsingTheSameCachedColdTableInstanceFromBeforeMigration_StillReadsCorrectly() {
        using (var seed = ColdStore.Open(dir).Unwrap()) {
            seed.OpenTable<int, Account>("accounts");
            await RunConfirmed(seed, () => StageInsert(seed, "accounts", 1, new Account(1, "alice@example.com", 100m)), lsn: 1);
        }

        using var store = ColdStore.Open(dir).Unwrap();
        // Opened (and cached) BEFORE migration - this is the exact instance that must still work
        // afterward, proving the dbi-handle rotation (IColdTableDbiHandle.UpdateDbi) is wired correctly,
        // since a drop-with-delete closes the old handle and reopening returns a fresh one.
        var accountsBeforeMigration = store.OpenTable<int, Account>("accounts");
        store.CompleteRecovery();

        store.RunMigration(targetGeneration: 1, [("accounts", DoubleBalance)]);

        Assert.That(store.Peek(accountsBeforeMigration, 1).Unwrap().Balance, Is.EqualTo(200m));
        Assert.That(store.OpenTable<int, Account>("accounts"), Is.SameAs(accountsBeforeMigration),
            "OpenTable's per-instance cache must survive a migration, not be invalidated by it.");
    }

    [Test]
    public async Task RunMigration_ATransformThatChangesTheKey_TheOldKeyIsGenuinelyGone() {
        using (var seed = ColdStore.Open(dir).Unwrap()) {
            seed.OpenTable<int, Account>("accounts");
            await RunConfirmed(seed, () => StageInsert(seed, "accounts", 1, new Account(1, "alice@example.com", 100m)), lsn: 1);
        }

        using var store = ColdStore.Open(dir).Unwrap();
        var accounts = store.OpenTable<int, Account>("accounts");
        store.CompleteRecovery();

        static (byte[] Key, byte[] Row) RekeyTo999(byte[] keyBytes, byte[] rowBytes) => (MemoryPackSerializer.Serialize(999), rowBytes);
        store.RunMigration(targetGeneration: 1, [("accounts", RekeyTo999)]);

        Assert.That(store.Peek(accounts, 1).IsError(), Is.True, "The pre-migration key must not survive a transform that changes it.");
        Assert.That(store.Peek(accounts, 999).Unwrap(), Is.EqualTo(new Account(1, "alice@example.com", 100m)));
    }

    [Test]
    public void RunMigration_ATableWithNoRows_StillSucceedsAndWritesTheGeneration() {
        using var store = ColdStore.Open(dir).Unwrap();
        store.OpenTable<int, Account>("accounts");

        var result = store.RunMigration(targetGeneration: 3, [("accounts", DoubleBalance)]);

        Assert.That(result.IsOk(), Is.True);
        Assert.That(store.ReadGeneration().Unwrap(), Is.EqualTo(3));
    }

    [Test]
    public async Task RunMigration_TwoTablesInOneCall_BothAreTransformedAtomically() {
        using (var seed = ColdStore.Open(dir).Unwrap()) {
            seed.OpenTable<int, Account>("accounts");
            seed.OpenTable<int, Account>("savings");
            await RunConfirmed(seed, () => {
                StageInsert(seed, "accounts", 1, new Account(1, "alice@example.com", 100m));
                StageInsert(seed, "savings", 1, new Account(1, "alice@example.com", 500m));
            }, lsn: 1);
        }

        using var store = ColdStore.Open(dir).Unwrap();
        var accounts = store.OpenTable<int, Account>("accounts");
        var savings = store.OpenTable<int, Account>("savings");
        store.CompleteRecovery();

        var result = store.RunMigration(targetGeneration: 1, [("accounts", DoubleBalance), ("savings", DoubleBalance)]);

        Assert.That(result.IsOk(), Is.True);
        Assert.That(store.Peek(accounts, 1).Unwrap().Balance, Is.EqualTo(200m));
        Assert.That(store.Peek(savings, 1).Unwrap().Balance, Is.EqualTo(1000m));
    }

    [Test]
    public void ReadGeneration_BeforeAnyMigration_DefaultsToZero() {
        using var store = ColdStore.Open(dir).Unwrap();

        Assert.That(store.ReadGeneration().Unwrap(), Is.EqualTo(0));
    }

    [Test]
    public async Task RunMigration_AnOrphanTable_IsDroppedEntirelyWhileOtherTablesStillMigrateNormally() {
        using (var seed = ColdStore.Open(dir).Unwrap()) {
            seed.OpenTable<int, Account>("accounts");
            seed.OpenTable<int, Account>("orphaned");
            await RunConfirmed(seed, () => {
                StageInsert(seed, "accounts", 1, new Account(1, "alice@example.com", 100m));
                StageInsert(seed, "orphaned", 1, new Account(1, "some-leftover-row@example.com", 999m));
            }, lsn: 1);
        }

        using var store = ColdStore.Open(dir).Unwrap();
        var accounts = store.OpenTable<int, Account>("accounts");
        store.OpenTable<int, Account>("orphaned");
        var recoverResult = store.CompleteRecovery();
        Assert.That(recoverResult.IsOk(), Is.True);

        var result = store.RunMigration(
            targetGeneration: 2,
            [("accounts", DoubleBalance)],
            orphanTablesToDrop: ["orphaned"]);

        Assert.That(result.IsOk(), Is.True);
        Assert.That(store.Peek(accounts, 1).Unwrap().Balance, Is.EqualTo(200m), "Non-orphaned tables must still migrate normally alongside a drop.");

        var reopenedOrphan = store.OpenTable<int, Account>("orphaned");
        Assert.That(store.ScanAll(reopenedOrphan).ToList(), Is.Empty,
            "A dropped table's name is fully free again - reopening it must find a fresh, empty table, not the old data.");
    }
}
