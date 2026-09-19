using MemoryPack;
using RhinoDB.Core;
using RhinoDB.Lib.Durability;
using RhinoDB.Lib.Execution;
using RhinoDB.Lib.Tables;

namespace RhinoDB.Lib.Cold.Test;

// ColdStore is the per-DbContext durability owner, per Docs/05-wal-design.md - generated
// code (via TableGenerator) drives it directly through its public surface (OpenTable/Stage/
// Peek/ScanAll/CompleteRecovery/IsScopeActive). Writes no longer touch libmdbx synchronously:
// Stage buffers an operation's changes in memory, EndScope hands them to the WriteAheadLog
// (Confirmed waits on its group fsync, Optimistic doesn't), and libmdbx only sees them via
// CompleteRecovery's replay (on reopen) - there's no automatic live checkpointing yet (a
// deliberately deferred follow-up, see the plan), so within a single open ColdStore instance,
// Peek/ScanAll against libmdbx only reflect a *previous* session's recovered writes, not the
// current session's, until the process is closed and reopened. These tests exercise real
// libmdbx and a real WAL file against a real temp directory - no mocking layer, matching how
// every other stage in this repo has been proven.
public class ColdStoreTests {
    private string dir = "";

    [SetUp]
    public void SetUp() {
        dir = Path.Combine(Path.GetTempPath(), "rhinodb-coldstore-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
    }

    [TearDown]
    public void TearDown() {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    static private void Stage(ColdStore store, string tableName, ChangeKind kind, int key, Account? row) =>
        store.Stage(TableIdHash.Compute(tableName), kind, MemoryPackSerializer.Serialize(key), row is null ? null : MemoryPackSerializer.Serialize(row.Value));

    static private async Task<DbError?> RunConfirmed(ColdStore store, Action stageActions, long lsn = 1) {
        store.BeginScope();
        stageActions();
        return await store.EndScope(commit: true, PropagationMode.Confirmed, lsn);
    }

    [Test]
    public void Open_AgainstAFreshDirectory_Succeeds() {
        var result = ColdStore.Open(dir);

        Assert.That(result.IsOk(), Is.True);
        result.Unwrap().Dispose();
    }

    [Test]
    public void Open_AgainstADirectoryThatDoesNotExistYet_CreatesItAndSucceeds() {
        var missing = Path.Combine(dir, "does-not-exist-yet");

        var result = ColdStore.Open(missing);

        Assert.That(result.IsOk(), Is.True);
        result.Unwrap().Dispose();
    }

    [Test]
    public void Open_AgainstADirectoryThatAlreadyContainsAnUnrelatedFile_StillSucceeds() {
        File.WriteAllText(Path.Combine(dir, "leftover.txt"), "");

        var result = ColdStore.Open(dir);

        Assert.That(result.IsOk(), Is.True);
        result.Unwrap().Dispose();
    }

    [Test]
    public void OpenTable_SameNameTwiceWithinOneInstance_ReturnsTheSameColdTableInstance() {
        using var store = ColdStore.Open(dir).Unwrap();

        var first = store.OpenTable<int, Account>("accounts");
        var second = store.OpenTable<int, Account>("accounts");

        Assert.That(second, Is.SameAs(first));
    }

    // ---- Scope tracking (what generated Apply()'s NoActiveTransaction guard checks) ----

    [Test]
    public void IsScopeActive_BeforeAnyBeginScope_IsFalse() {
        using var store = ColdStore.Open(dir).Unwrap();

        Assert.That(store.IsScopeActive, Is.False);
    }

    [Test]
    public async Task BeginScope_SetsIsScopeActive_EndScopeClearsIt() {
        using var store = ColdStore.Open(dir).Unwrap();

        store.BeginScope();
        Assert.That(store.IsScopeActive, Is.True);

        await store.EndScope(commit: true, PropagationMode.Optimistic, lsn: 1);
        Assert.That(store.IsScopeActive, Is.False);
    }

    // ---- EndScope: staging, commit/discard, Confirmed vs Optimistic ----

    [Test]
    public async Task EndScope_WithNothingStaged_CompletesImmediatelyWithNoError() {
        using var store = ColdStore.Open(dir).Unwrap();

        store.BeginScope();
        var error = await store.EndScope(commit: true, PropagationMode.Confirmed, lsn: 1);

        Assert.That(error, Is.Null);
    }

    [Test]
    public async Task EndScope_WithCommitFalse_DiscardsStagedChangesWithoutTouchingTheWal() {
        var walPath = Path.Combine(dir, "wal.dat");
        using (var store = ColdStore.Open(dir).Unwrap()) {
            store.BeginScope();
            Stage(store, "accounts", ChangeKind.Insert, 1, new Account(1, "alice@example.com", 100m));
            var error = await store.EndScope(commit: false, PropagationMode.Confirmed, lsn: 1);
            Assert.That(error, Is.Null);
        }

        var bytes = File.ReadAllBytes(walPath);
        Assert.That(bytes.Length, Is.EqualTo(WalFileHeaderCodec.Size), "A discarded (commit: false) operation must never reach the WAL at all.");
    }

    [Test]
    public async Task EndScope_Confirmed_WaitsForItsGroupFsyncBeforeCompleting() {
        using var store = ColdStore.Open(dir).Unwrap();
        var error = await RunConfirmed(store, () => Stage(store, "accounts", ChangeKind.Insert, 1, new Account(1, "alice@example.com", 100m)));

        Assert.That(error, Is.Null);
    }

    // ---- CompleteRecovery: WAL tail -> mdbx, exercised by closing and reopening ----

    [Test]
    public async Task CompleteRecovery_AfterAConfirmedInsertAndReopen_MakesTheRowVisibleToPeek() {
        using (var store = ColdStore.Open(dir).Unwrap()) {
            var accounts = store.OpenTable<int, Account>("accounts");
            var error = await RunConfirmed(store, () => Stage(store, "accounts", ChangeKind.Insert, 1, new Account(1, "alice@example.com", 100m)));
            Assert.That(error, Is.Null);
            // Peek reads mdbx directly - nothing is there yet within this same session, since
            // there's no automatic live checkpointing (a deliberately deferred follow-up).
            Assert.That(store.Peek(accounts, 1).IsError(), Is.True);
        }

        using var reopened = ColdStore.Open(dir).Unwrap();
        var reopenedAccounts = reopened.OpenTable<int, Account>("accounts");
        var recoverResult = reopened.CompleteRecovery();

        Assert.That(recoverResult.IsOk(), Is.True);
        Assert.That(reopenedAccounts, Is.SameAs(reopened.OpenTable<int, Account>("accounts")));
        Assert.That(reopened.Peek(reopenedAccounts, 1).Unwrap(), Is.EqualTo(new Account(1, "alice@example.com", 100m)));
    }

    [Test]
    public async Task CompleteRecovery_ADeleteAfterReopen_LeavesTheRowGenuinelyGone() {
        using (var store = ColdStore.Open(dir).Unwrap()) {
            store.OpenTable<int, Account>("accounts");
            await RunConfirmed(store, () => Stage(store, "accounts", ChangeKind.Insert, 1, new Account(1, "alice@example.com", 100m)), lsn: 1);
            await RunConfirmed(store, () => Stage(store, "accounts", ChangeKind.Delete, 1, null), lsn: 2);
        }

        using var reopened = ColdStore.Open(dir).Unwrap();
        var accounts = reopened.OpenTable<int, Account>("accounts");
        reopened.CompleteRecovery();

        Assert.That(reopened.Peek(accounts, 1).IsError(), Is.True);
    }

    [Test]
    public async Task CompleteRecovery_AKeyDeletedThenReinsertedWithinTheSameTail_ResolvesToTheReinsertedValue() {
        // Pins the last-write-wins bucketing this relies on: naively feeding every Put and
        // every Delete seen in the tail to RunCheckpoint independently (put-then-delete order)
        // would incorrectly leave this key deleted, since RunCheckpoint's own ordering assumes
        // each key appears in at most one of the two sets.
        using (var store = ColdStore.Open(dir).Unwrap()) {
            store.OpenTable<int, Account>("accounts");
            await RunConfirmed(store, () => Stage(store, "accounts", ChangeKind.Insert, 1, new Account(1, "alice@example.com", 100m)), lsn: 1);
            await RunConfirmed(store, () => Stage(store, "accounts", ChangeKind.Delete, 1, null), lsn: 2);
            await RunConfirmed(store, () => Stage(store, "accounts", ChangeKind.Insert, 1, new Account(1, "alice-reinserted@example.com", 250m)), lsn: 3);
        }

        using var reopened = ColdStore.Open(dir).Unwrap();
        var accounts = reopened.OpenTable<int, Account>("accounts");
        reopened.CompleteRecovery();

        Assert.That(reopened.Peek(accounts, 1).Unwrap(), Is.EqualTo(new Account(1, "alice-reinserted@example.com", 250m)));
    }

    [Test]
    public void CompleteRecovery_WithNothingToRecover_IsANoOp() {
        using var store = ColdStore.Open(dir).Unwrap();

        var result = store.CompleteRecovery();

        Assert.That(result.IsOk(), Is.True);
    }

    [Test]
    public async Task CompleteRecovery_CalledTwice_IsIdempotent() {
        using (var store = ColdStore.Open(dir).Unwrap()) {
            store.OpenTable<int, Account>("accounts");
            await RunConfirmed(store, () => Stage(store, "accounts", ChangeKind.Insert, 1, new Account(1, "alice@example.com", 100m)));
        }

        using var reopened = ColdStore.Open(dir).Unwrap();
        var accounts = reopened.OpenTable<int, Account>("accounts");

        var first = reopened.CompleteRecovery();
        var second = reopened.CompleteRecovery();

        Assert.That(first.IsOk(), Is.True);
        Assert.That(second.IsOk(), Is.True);
        Assert.That(reopened.Peek(accounts, 1).Unwrap(), Is.EqualTo(new Account(1, "alice@example.com", 100m)));
    }

    [Test]
    public async Task CompleteRecovery_TwoDifferentTables_EachRecoversUnderItsOwnDbi() {
        using (var store = ColdStore.Open(dir).Unwrap()) {
            store.OpenTable<int, Account>("accounts");
            store.OpenTable<int, Account>("other-accounts");
            await RunConfirmed(store, () => {
                Stage(store, "accounts", ChangeKind.Insert, 1, new Account(1, "alice@example.com", 100m));
                Stage(store, "other-accounts", ChangeKind.Insert, 2, new Account(2, "bob@example.com", 200m));
            });
        }

        using var reopened = ColdStore.Open(dir).Unwrap();
        var accounts = reopened.OpenTable<int, Account>("accounts");
        var otherAccounts = reopened.OpenTable<int, Account>("other-accounts");
        reopened.CompleteRecovery();

        Assert.That(reopened.Peek(accounts, 1).Unwrap(), Is.EqualTo(new Account(1, "alice@example.com", 100m)));
        Assert.That(reopened.Peek(accounts, 2).IsError(), Is.True, "A write to a different table must not appear under this one.");
        Assert.That(reopened.Peek(otherAccounts, 2).Unwrap(), Is.EqualTo(new Account(2, "bob@example.com", 200m)));
    }

    // ---- Peek/ScanAll: read-only surface, independent of any active scope ----

    [Test]
    public void Peek_AnAbsentKey_ReturnsError() {
        using var store = ColdStore.Open(dir).Unwrap();
        var accounts = store.OpenTable<int, Account>("accounts");

        var peek = store.Peek(accounts, 999);

        Assert.That(peek.IsError(), Is.True);
    }

    [Test]
    public void ScanAll_OnAnEmptyTable_ReturnsNoRows() {
        using var store = ColdStore.Open(dir).Unwrap();
        var accounts = store.OpenTable<int, Account>("accounts");

        var scanned = store.ScanAll(accounts).ToList();

        Assert.That(scanned, Is.Empty);
    }

    [Test]
    public async Task ScanAll_AfterRecovery_ReturnsEveryRecoveredRow() {
        using (var store = ColdStore.Open(dir).Unwrap()) {
            store.OpenTable<int, Account>("accounts");
            await RunConfirmed(store, () => {
                Stage(store, "accounts", ChangeKind.Insert, 1, new Account(1, "alice@example.com", 100m));
                Stage(store, "accounts", ChangeKind.Insert, 2, new Account(2, "bob@example.com", 200m));
            });
        }

        using var reopened = ColdStore.Open(dir).Unwrap();
        var accounts = reopened.OpenTable<int, Account>("accounts");
        reopened.CompleteRecovery();

        var scanned = reopened.ScanAll(accounts).ToList();

        Assert.That(scanned, Has.Count.EqualTo(2));
        Assert.That(scanned, Has.Some.Matches<(int Key, Account Row)>(p => p.Key == 1 && p.Row.Equals(new Account(1, "alice@example.com", 100m))));
        Assert.That(scanned, Has.Some.Matches<(int Key, Account Row)>(p => p.Key == 2 && p.Row.Equals(new Account(2, "bob@example.com", 200m))));
    }

    [Test]
    public async Task ScanAll_TwoTablesConcurrently_BothCompleteWithCorrectData() {
        // libmdbx is multi-reader - two ScanAll calls against different tables must be able to
        // run at the same time without one blocking or corrupting the other.
        using (var store = ColdStore.Open(dir).Unwrap()) {
            store.OpenTable<int, Account>("accounts");
            store.OpenTable<int, Account>("other-accounts");
            await RunConfirmed(store, () => {
                Stage(store, "accounts", ChangeKind.Insert, 1, new Account(1, "alice@example.com", 100m));
                Stage(store, "other-accounts", ChangeKind.Insert, 2, new Account(2, "bob@example.com", 200m));
            });
        }

        using var reopened = ColdStore.Open(dir).Unwrap();
        var accounts = reopened.OpenTable<int, Account>("accounts");
        var otherAccounts = reopened.OpenTable<int, Account>("other-accounts");
        reopened.CompleteRecovery();

        var firstTask = Task.Run(() => reopened.ScanAll(accounts).ToList());
        var secondTask = Task.Run(() => reopened.ScanAll(otherAccounts).ToList());
        Task.WaitAll(firstTask, secondTask);

        Assert.That(firstTask.Result, Has.Count.EqualTo(1));
        Assert.That(firstTask.Result[0].Row, Is.EqualTo(new Account(1, "alice@example.com", 100m)));
        Assert.That(secondTask.Result, Has.Count.EqualTo(1));
        Assert.That(secondTask.Result[0].Row, Is.EqualTo(new Account(2, "bob@example.com", 200m)));
    }

    [Test]
    public async Task CompleteRecovery_AChangeForATableThatIsNotOpen_RefusesRatherThanDroppingItAndTruncating() {
        // A tail entry naming a table this ColdStore never opened means these bytes were written
        // under a different schema contract (drift, a foreign WAL file, or a tableId collision).
        // Skipping it would be silent data loss, because recovery also truncates the WAL.
        using (var store = ColdStore.Open(dir).Unwrap()) {
            store.OpenTable<int, Account>("accounts");
            await RunConfirmed(store, () => {
                Stage(store, "accounts", ChangeKind.Insert, 1, new Account(1, "alice@example.com", 100m));
                Stage(store, "sigma", ChangeKind.Insert, 2, new Account(2, "bob@example.com", 200m));
            });
        }

        using (var reopened = ColdStore.Open(dir).Unwrap()) {
            var accounts = reopened.OpenTable<int, Account>("accounts");

            var refused = await reopened.CompleteRecoveryAsync();

            Assert.That(refused.IsError(), Is.True);
            Assert.That(refused.GetError().Kind, Is.EqualTo(ErrorKind.SystemFailure));
            Assert.That(reopened.Peek(accounts, 1).IsError(), Is.True, "All-or-nothing: nothing was checkpointed while an unknown table was in the way.");
        }

        using var recovered = ColdStore.Open(dir).Unwrap();
        var recoveredAccounts = recovered.OpenTable<int, Account>("accounts");
        var sigma = recovered.OpenTable<int, Account>("sigma");

        var result = await recovered.CompleteRecoveryAsync();

        Assert.That(result.IsOk(), Is.True, "The refused tail must still be on disk: opening the missing table makes the same recovery succeed.");
        Assert.That(recovered.Peek(recoveredAccounts, 1).Unwrap(), Is.EqualTo(new Account(1, "alice@example.com", 100m)));
        Assert.That(recovered.Peek(sigma, 2).Unwrap(), Is.EqualTo(new Account(2, "bob@example.com", 200m)));
    }
}
