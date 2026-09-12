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
}
