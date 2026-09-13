namespace RhinoDB.Lib.Cold.Test;

// ColdStore is the per-DbContext libmdbx environment wrapper, per
// Docs/02-architecture.md "Cold storage" - generated code (via TableGenerator)
// drives it directly now, through a narrow public surface (OpenTable/Put/Get/
// Delete/Peek/IsScopeActive). This suite exercises the LOWER-level, still-internal
// primitives directly instead (RhinoDB.Lib.Test sees them via InternalsVisibleTo,
// same as every other internal-surface suite in this project) - EnsureWriteTxn/
// BeginScope/EndScope/ColdTable's own Put/Get(Transaction,...) overloads - since
// those never cross the assembly boundary generated code lives behind. These tests
// exercise real libmdbx via the Part A native binding against a real temp
// directory - no mocking layer, matching how every other stage in this repo has
// been proven (e.g. Test.Native's real dotnet test runs against the actual mdbx.dll).
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

    [Test]
    public void Open_AgainstAFreshDirectory_Succeeds() {
        var result = ColdStore.Open(dir);

        Assert.That(result.IsOk(), Is.True);
        result.Unwrap().Dispose();
    }

    [Test]
    public void Open_AgainstADirectoryThatDoesNotExistYet_CreatesItAndSucceeds() {
        // libmdbx creates a missing target directory itself (confirmed here, not assumed) -
        // the isFreshDirectory check (Directory.Exists(path) before Open()) still correctly
        // identifies this as fresh and doesn't throw despite the path not existing yet.
        var missing = Path.Combine(dir, "does-not-exist-yet");

        var result = ColdStore.Open(missing);

        Assert.That(result.IsOk(), Is.True);
        result.Unwrap().Dispose();
    }

    [Test]
    public void Open_AgainstADirectoryThatAlreadyContainsAnUnrelatedFile_StillSucceeds() {
        // The isFreshDirectory heuristic (Docs/Dev/RhinoDB.Lib/Cold/ColdStore.md) treats
        // "any pre-existing file" as "not fresh" and skips the one-time directory-fsync -
        // this must not be confused for a real failure.
        File.WriteAllText(Path.Combine(dir, "leftover.txt"), "");

        var result = ColdStore.Open(dir);

        Assert.That(result.IsOk(), Is.True);
        result.Unwrap().Dispose();
    }

    [Test]
    public void OpenTable_TwoDifferentNames_WritesUnderOneNameDoNotAppearUnderTheOther() {
        using var store = ColdStore.Open(dir).Unwrap();
        var accounts = store.OpenTable<int, Account>("accounts");
        var otherAccounts = store.OpenTable<int, Account>("other-accounts");

        var txn = store.EnsureWriteTxn();
        accounts.Put(txn, 1, new Account(1, "alice@example.com", 100m));
        store.EndScope(commit: true, forceSync: false);

        var readTxn = store.EnsureWriteTxn();
        Assert.That(accounts.Get(readTxn, 1).IsOk(), Is.True);
        Assert.That(otherAccounts.Get(readTxn, 1).IsError(), Is.True);
        store.EndScope(commit: true, forceSync: false);
    }

    [Test]
    public void OpenTable_SameNameTwiceWithinOneInstance_IsMemoized_WriteThroughOneHandleVisibleThroughTheOther() {
        using var store = ColdStore.Open(dir).Unwrap();
        var first = store.OpenTable<int, Account>("accounts");
        var second = store.OpenTable<int, Account>("accounts");

        var txn = store.EnsureWriteTxn();
        first.Put(txn, 1, new Account(1, "alice@example.com", 100m));
        store.EndScope(commit: true, forceSync: false);

        var readTxn = store.EnsureWriteTxn();
        Assert.That(second.Get(readTxn, 1).Unwrap(), Is.EqualTo(new Account(1, "alice@example.com", 100m)));
        store.EndScope(commit: true, forceSync: false);
    }

    [Test]
    public void OpenTable_SameNameTwiceWithinOneInstance_ReturnsTheSameColdTableInstance() {
        using var store = ColdStore.Open(dir).Unwrap();

        var first = store.OpenTable<int, Account>("accounts");
        var second = store.OpenTable<int, Account>("accounts");

        Assert.That(second, Is.SameAs(first));
    }

    [Test]
    public void OpenTable_SameName_ReopeningTheStoreAgainstTheSameDirectory_ResolvesTheSamePersistedData() {
        using (var store = ColdStore.Open(dir).Unwrap()) {
            var accounts = store.OpenTable<int, Account>("accounts");
            var txn = store.EnsureWriteTxn();
            accounts.Put(txn, 1, new Account(1, "alice@example.com", 100m));
            store.EndScope(commit: true, forceSync: true);
        }

        using (var reopened = ColdStore.Open(dir).Unwrap()) {
            var accounts = reopened.OpenTable<int, Account>("accounts");
            var readTxn = reopened.EnsureWriteTxn();

            Assert.That(accounts.Get(readTxn, 1).Unwrap(), Is.EqualTo(new Account(1, "alice@example.com", 100m)));
            reopened.EndScope(commit: true, forceSync: false);
        }
    }

    [Test]
    public void EnsureWriteTxn_CalledTwiceWithoutEndScope_ReturnsTheSameAmbientTransaction() {
        // Lazy, per-operation-scope ambient txn (Docs/02-architecture.md "Cold
        // storage" / this plan's decision 5 & Part C) - two PersistentTable calls
        // within the same Run must share one write txn, not open a second one
        // (libmdbx only allows one write txn at a time per environment anyway).
        using var store = ColdStore.Open(dir).Unwrap();

        var first = store.EnsureWriteTxn();
        var second = store.EnsureWriteTxn();

        Assert.That(second, Is.SameAs(first));
        store.EndScope(commit: false, forceSync: false);
    }

    [Test]
    public void EndScope_WithCommitFalse_AbortsAndDiscardsTheWrite() {
        using var store = ColdStore.Open(dir).Unwrap();
        var accounts = store.OpenTable<int, Account>("accounts");

        var txn = store.EnsureWriteTxn();
        accounts.Put(txn, 1, new Account(1, "alice@example.com", 100m));
        store.EndScope(commit: false, forceSync: false);

        var readTxn = store.EnsureWriteTxn();
        Assert.That(accounts.Get(readTxn, 1).IsError(), Is.True);
        store.EndScope(commit: true, forceSync: false);
    }

    [Test]
    public void EndScope_ClearsTheAmbientTransaction_NextEnsureWriteTxnOpensAFreshOne() {
        using var store = ColdStore.Open(dir).Unwrap();

        var txn1 = store.EnsureWriteTxn();
        store.EndScope(commit: true, forceSync: false);
        var txn2 = store.EnsureWriteTxn();

        Assert.That(txn2, Is.Not.SameAs(txn1));
        store.EndScope(commit: true, forceSync: false);
    }

    // ---- Scope tracking (what generated Apply()'s NoActiveTransaction guard checks) ----

    [Test]
    public void IsScopeActive_BeforeAnyBeginScope_IsFalse() {
        using var store = ColdStore.Open(dir).Unwrap();

        Assert.That(store.IsScopeActive, Is.False);
    }

    [Test]
    public void BeginScope_SetsIsScopeActive_EndScopeClearsIt() {
        using var store = ColdStore.Open(dir).Unwrap();

        store.BeginScope();
        Assert.That(store.IsScopeActive, Is.True);

        store.EndScope(commit: true, forceSync: false);
        Assert.That(store.IsScopeActive, Is.False);
    }

    // ---- Put/Get/Delete/Peek: the public wrapper surface generated code drives
    // directly (added when PersistentTable<TKey,TRow> was retired in favor of
    // codegen driving cold storage itself) - each wraps the ambient-txn machinery
    // above without exposing RhinoDB.Native.Transaction across the assembly
    // boundary. Callers check IsScopeActive themselves first (these three don't
    // check it internally), so BeginScope() is called explicitly here too. ----

    [Test]
    public void Put_ThenGet_RoundTripsTheValue() {
        using var store = ColdStore.Open(dir).Unwrap();
        var accounts = store.OpenTable<int, Account>("accounts");
        store.BeginScope();

        var put = store.Put(accounts, 1, new Account(1, "alice@example.com", 100m));
        var get = store.Get(accounts, 1);

        Assert.That(put.IsOk(), Is.True);
        Assert.That(get.Unwrap(), Is.EqualTo(new Account(1, "alice@example.com", 100m)));
        store.EndScope(commit: true, forceSync: false);
    }

    [Test]
    public void Get_AGenuinelyAbsentKey_ReturnsError() {
        using var store = ColdStore.Open(dir).Unwrap();
        var accounts = store.OpenTable<int, Account>("accounts");
        store.BeginScope();

        var get = store.Get(accounts, 999);

        Assert.That(get.IsError(), Is.True);
        store.EndScope(commit: true, forceSync: false);
    }

    [Test]
    public void Delete_ThenGet_ReturnsError() {
        using var store = ColdStore.Open(dir).Unwrap();
        var accounts = store.OpenTable<int, Account>("accounts");
        store.BeginScope();
        store.Put(accounts, 1, new Account(1, "alice@example.com", 100m));

        var delete = store.Delete(accounts, 1);
        var get = store.Get(accounts, 1);

        Assert.That(delete.IsOk(), Is.True);
        Assert.That(get.IsError(), Is.True);
        store.EndScope(commit: true, forceSync: false);
    }

    [Test]
    public void Peek_AfterCommit_ReadsTheValueWithoutRequiringAnActiveScope() {
        using var store = ColdStore.Open(dir).Unwrap();
        var accounts = store.OpenTable<int, Account>("accounts");
        store.BeginScope();
        store.Put(accounts, 1, new Account(1, "alice@example.com", 100m));
        store.EndScope(commit: true, forceSync: false);

        // No BeginScope() here - Peek opens its own independent read-only txn.
        var peek = store.Peek(accounts, 1);

        Assert.That(peek.Unwrap(), Is.EqualTo(new Account(1, "alice@example.com", 100m)));
    }

    // ---- ScanAll: full-table scan for eager loading, no active scope required ----

    [Test]
    public void ScanAll_ReturnsEveryPersistedRow_WithoutRequiringAnActiveScope() {
        using var store = ColdStore.Open(dir).Unwrap();
        var accounts = store.OpenTable<int, Account>("accounts");
        store.BeginScope();
        store.Put(accounts, 1, new Account(1, "alice@example.com", 100m));
        store.Put(accounts, 2, new Account(2, "bob@example.com", 200m));
        store.EndScope(commit: true, forceSync: false);

        var scanned = store.ScanAll(accounts).ToList();

        Assert.That(scanned, Has.Count.EqualTo(2));
        Assert.That(scanned, Has.Some.Matches<(int Key, Account Row)>(p => p.Key == 1 && p.Row.Equals(new Account(1, "alice@example.com", 100m))));
        Assert.That(scanned, Has.Some.Matches<(int Key, Account Row)>(p => p.Key == 2 && p.Row.Equals(new Account(2, "bob@example.com", 200m))));
    }

    [Test]
    public void ScanAll_OnAnEmptyTable_ReturnsNoRows() {
        using var store = ColdStore.Open(dir).Unwrap();
        var accounts = store.OpenTable<int, Account>("accounts");

        var scanned = store.ScanAll(accounts).ToList();

        Assert.That(scanned, Is.Empty);
    }

    [Test]
    public void ScanAll_DoesNotSeeARowThatWasPutThenAborted() {
        using var store = ColdStore.Open(dir).Unwrap();
        var accounts = store.OpenTable<int, Account>("accounts");
        store.BeginScope();
        store.Put(accounts, 1, new Account(1, "alice@example.com", 100m));
        store.EndScope(commit: false, forceSync: false);

        // The write txn is fully closed (aborted) before this runs - calling
        // ScanAll on the same thread WHILE a write txn is still open would
        // fail with MDBX_TXN_OVERLAPPING (libmdbx ties a txn to the OS
        // thread that opened it and refuses an overlapping read+write pair
        // on that thread) - never an issue for real eager-loading, since the
        // loader runs before any write scope exists at all.
        var scanned = store.ScanAll(accounts).ToList();

        Assert.That(scanned, Is.Empty);
    }

    // ---- Confirmed commit-coalescing window (Docs/02-architecture.md "Confirmed
    // commit batching") - opt-in via Open's commitCoalescingWindow, default Zero
    // preserves the exact prior one-sync-call-per-Confirmed-commit behavior
    // (already covered by every test above that never passes this parameter). ----

    [Test]
    public void EndScope_WithCoalescingWindow_TwoConfirmedCommitsWithinTheWindow_ShareOneSyncCall() {
        using var store = ColdStore.Open(dir, commitCoalescingWindow: TimeSpan.FromMilliseconds(200)).Unwrap();
        var accounts = store.OpenTable<int, Account>("accounts");

        store.BeginScope();
        var txn1 = store.EnsureWriteTxn();
        accounts.Put(txn1, 1, new Account(1, "alice@example.com", 100m));
        var syncTask1 = store.EndScope(commit: true, forceSync: true);

        store.BeginScope();
        var txn2 = store.EnsureWriteTxn();
        accounts.Put(txn2, 2, new Account(2, "bob@example.com", 200m));
        var syncTask2 = store.EndScope(commit: true, forceSync: true);

        Assert.That(syncTask2, Is.SameAs(syncTask1), "Two Confirmed commits arriving within the coalescing window should share exactly one physical sync call.");
        Assert.That(syncTask1.Result, Is.EqualTo(0));
    }

    [Test]
    public async Task EndScope_WithCoalescingWindow_ACommitAfterTheWindowElapses_GetsItsOwnSyncCall() {
        using var store = ColdStore.Open(dir, commitCoalescingWindow: TimeSpan.FromMilliseconds(20)).Unwrap();
        var accounts = store.OpenTable<int, Account>("accounts");

        store.BeginScope();
        var txn1 = store.EnsureWriteTxn();
        accounts.Put(txn1, 1, new Account(1, "alice@example.com", 100m));
        var syncTask1 = store.EndScope(commit: true, forceSync: true);
        await syncTask1;

        store.BeginScope();
        var txn2 = store.EnsureWriteTxn();
        accounts.Put(txn2, 2, new Account(2, "bob@example.com", 200m));
        var syncTask2 = store.EndScope(commit: true, forceSync: true);

        Assert.That(syncTask2, Is.Not.SameAs(syncTask1), "A commit arriving after the window has already closed must get its own sync call, not join a stale one.");
        Assert.That((await syncTask2), Is.EqualTo(0));
    }

    [Test]
    public void ScanAll_TwoTablesConcurrently_BothCompleteWithCorrectData() {
        // libmdbx is multi-reader - two ScanAll calls against different
        // tables must be able to run at the same time without one blocking
        // or corrupting the other.
        using var store = ColdStore.Open(dir).Unwrap();
        var accounts = store.OpenTable<int, Account>("accounts");
        var otherAccounts = store.OpenTable<int, Account>("other-accounts");
        store.BeginScope();
        store.Put(accounts, 1, new Account(1, "alice@example.com", 100m));
        store.Put(otherAccounts, 2, new Account(2, "bob@example.com", 200m));
        store.EndScope(commit: true, forceSync: false);

        var firstTask = Task.Run(() => store.ScanAll(accounts).ToList());
        var secondTask = Task.Run(() => store.ScanAll(otherAccounts).ToList());
        Task.WaitAll(firstTask, secondTask);

        Assert.That(firstTask.Result, Has.Count.EqualTo(1));
        Assert.That(firstTask.Result[0].Row, Is.EqualTo(new Account(1, "alice@example.com", 100m)));
        Assert.That(secondTask.Result, Has.Count.EqualTo(1));
        Assert.That(secondTask.Result[0].Row, Is.EqualTo(new Account(2, "bob@example.com", 200m)));
    }
}
