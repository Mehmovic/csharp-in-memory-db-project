using RhinoDB.Core;
using RhinoDB.Lib.Cold;
using RhinoDB.Lib.Execution;

namespace RhinoDB.Generators.Test;

// PersistentTable<TKey,TRow> is gone from the codebase entirely (retired
// 2026-09-12, the same "hand-prove then delete the scaffold" treatment
// Table<TKey,TRow> got) - its cold-storage decisions (Find/Update are
// memory-only, Insert unconditionally upserts over a cold-only key, Evict
// never touches cold storage) now live entirely in generated code. This
// suite ports the scenarios from the deleted Cold/PersistentTableTests.cs
// and Cold/LoadEvictPeekTests.cs that weren't already covered by
// Milestone3Tests.cs/PersistentSecondaryIndexTests.cs, proving those same
// decisions hold through the generated path with nothing left underneath.
public class PersistentDurabilityTests {
    private string dir = "";

    private const string Source = """
        using MemoryPack;
        using MessagePack;
        using RhinoDB.Core.Tables;
        using RhinoDB.Lib.Execution;

        namespace TestNs;

        [Database]
        public partial class VaultDb : DbContext<VaultDbTransaction> { }

        [Table(TableKind.Persistent, typeof(VaultDb), Evictable = true)]
        [MemoryPackable(GenerateType.VersionTolerant)]
        [MessagePackObject]
        public readonly partial record struct Account(
            [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
            [property: MemoryPackOrder(1)] [property: Key(1)] int OwnerId,
            [property: MemoryPackOrder(2)] [property: Key(2)] decimal Balance);

        // QuerySet/QuerySingle are ref structs and can never cross a dynamic call
        // boundary (see GeneratorTestHost.InvokeHelper) - typed helpers instead.
        public static class TestHelpers {
            public static bool AccountFindIsOk(VaultDbAccountOps ops, int id) => ops.Primary.Find(id).HasRow();
            public static decimal AccountBalance(VaultDbAccountOps ops, int id) => ops.Primary.Find(id).Get().Unwrap().Balance;
        }
        """;

    [SetUp]
    public void SetUp() {
        dir = Path.Combine(Path.GetTempPath(), "rhinodb-persistent-durability-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
    }

    [TearDown]
    public void TearDown() {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    static private (object Db, Type TxType, System.Reflection.Assembly Assembly) NewDb(ColdStore cold) {
        var (asm, _) = GeneratorTestHost.CompileAndLoad(Source);
        var dbType = asm.GetType("TestNs.VaultDb")!;
        var txType = asm.GetType("TestNs.VaultDbTransaction")!;
        var db = Activator.CreateInstance(dbType, cold)!;
        return (db, txType, asm);
    }

    static private object NewAccount(System.Reflection.Assembly assembly, int id, int ownerId, decimal balance) {
        var t = assembly.GetType("TestNs.Account")!;
        return Activator.CreateInstance(t, id, ownerId, balance)!;
    }

    // ---- Decision 1: Find never touches cold storage ----

    [Test]
    public async Task Get_ForARowThatOnlyExistsColdAndWasNeverLoaded_ReturnsNotFound() {
        System.Reflection.Assembly asm;
        Type dbType, txType;
        using (var cold = ColdStore.Open(dir).Unwrap()) {
            (asm, _) = GeneratorTestHost.CompileAndLoad(Source);
            dbType = asm.GetType("TestNs.VaultDb")!;
            txType = asm.GetType("TestNs.VaultDbTransaction")!;
            var db = Activator.CreateInstance(dbType, cold)!;
            await (Task<Result>)GeneratorTestHost.RunTransactional(
                db, txType, (ctx, tx) => { ((dynamic)tx).Account.Insert((dynamic)NewAccount(asm, 1, 1, 100m)); return Result.Ok(); },
                PropagationMode.Confirmed);
        }

        using var reopenedCold = ColdStore.Open(dir).Unwrap();
        var reopenedDb = Activator.CreateInstance(dbType, reopenedCold)!;

        var found = true;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            reopenedDb, txType, (ctx, tx) => { found = (bool)GeneratorTestHost.InvokeHelper(asm, "TestNs.TestHelpers", "AccountFindIsOk", (object)((dynamic)tx).Account, 1)!; return Result.Ok(); },
            PropagationMode.Optimistic);

        Assert.That(found, Is.False, "A fresh in-memory table has an empty primaryIndex even though the row is durably present - Find must not silently reach for cold storage.");
    }

    // ---- Decision 4: no implicit load-then-update ----

    [Test]
    public async Task Update_ForARowThatOnlyExistsColdAndWasNeverLoaded_ReturnsNotFound() {
        System.Reflection.Assembly asm;
        Type dbType, txType;
        using (var cold = ColdStore.Open(dir).Unwrap()) {
            (asm, _) = GeneratorTestHost.CompileAndLoad(Source);
            dbType = asm.GetType("TestNs.VaultDb")!;
            txType = asm.GetType("TestNs.VaultDbTransaction")!;
            var db = Activator.CreateInstance(dbType, cold)!;
            await (Task<Result>)GeneratorTestHost.RunTransactional(
                db, txType, (ctx, tx) => { ((dynamic)tx).Account.Insert((dynamic)NewAccount(asm, 1, 1, 100m)); return Result.Ok(); },
                PropagationMode.Confirmed);
        }

        using var reopenedCold = ColdStore.Open(dir).Unwrap();
        var reopenedDb = Activator.CreateInstance(dbType, reopenedCold)!;

        var result = await (Task<Result>)GeneratorTestHost.RunTransactional(
            reopenedDb, txType, (ctx, tx) => { ((dynamic)tx).Account.Update(1, (dynamic)NewAccount(asm, 1, 1, 500m)); return Result.Ok(); },
            PropagationMode.Optimistic);

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetError().Kind, Is.EqualTo(ErrorKind.IndexKeyNotFound));
    }

    // ---- Primary-key immutability ----

    [Test]
    public async Task Update_AttemptingToChangeThePrimaryKey_ReturnsPrimaryKeyImmutable() {
        using var cold = ColdStore.Open(dir).Unwrap();
        var (db, txType, asm) = NewDb(cold);

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Account.Insert((dynamic)NewAccount(asm, 1, 1, 100m)); return Result.Ok(); },
            PropagationMode.Optimistic);

        var result = await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Account.Update(1, (dynamic)NewAccount(asm, 2, 1, 100m)); return Result.Ok(); },
            PropagationMode.Optimistic);

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetError().Kind, Is.EqualTo(ErrorKind.PrimaryKeyImmutable));
    }

    // ---- Durability round trips not yet covered by Milestone3Tests.cs ----

    [Test]
    public async Task Insert_Optimistic_ThenCloseAndReopen_StorageLoadRoundTripsTheRow() {
        System.Reflection.Assembly asm;
        Type dbType, txType;
        using (var cold = ColdStore.Open(dir).Unwrap()) {
            (asm, _) = GeneratorTestHost.CompileAndLoad(Source);
            dbType = asm.GetType("TestNs.VaultDb")!;
            txType = asm.GetType("TestNs.VaultDbTransaction")!;
            var db = Activator.CreateInstance(dbType, cold)!;

            var insert = await (Task<Result>)GeneratorTestHost.RunTransactional(
                db, txType, (ctx, tx) => { ((dynamic)tx).Account.Insert((dynamic)NewAccount(asm, 1, 1, 100m)); return Result.Ok(); },
                PropagationMode.Optimistic);
            Assert.That(insert.IsOk(), Is.True);
        }

        using var reopenedCold = ColdStore.Open(dir).Unwrap();
        var reopenedDb = Activator.CreateInstance(dbType, reopenedCold)!;
        reopenedCold.CompleteRecovery();

        bool loaded = false, found = false;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            reopenedDb, txType, (ctx, tx) => {
                dynamic dtx = tx;
                loaded = dtx.Account.Storage.Load(1).IsOk();
                found = (bool)GeneratorTestHost.InvokeHelper(asm, "TestNs.TestHelpers", "AccountFindIsOk", (object)dtx.Account, 1)!;
                return Result.Ok();
            }, PropagationMode.Optimistic);

        Assert.That(loaded, Is.True);
        Assert.That(found, Is.True);
    }

    [Test]
    public async Task Delete_ThenCloseAndReopen_StorageLoadReturnsNotFound() {
        System.Reflection.Assembly asm;
        Type dbType, txType;
        using (var cold = ColdStore.Open(dir).Unwrap()) {
            (asm, _) = GeneratorTestHost.CompileAndLoad(Source);
            dbType = asm.GetType("TestNs.VaultDb")!;
            txType = asm.GetType("TestNs.VaultDbTransaction")!;
            var db = Activator.CreateInstance(dbType, cold)!;

            await (Task<Result>)GeneratorTestHost.RunTransactional(
                db, txType, (ctx, tx) => { ((dynamic)tx).Account.Insert((dynamic)NewAccount(asm, 1, 1, 100m)); return Result.Ok(); },
                PropagationMode.Confirmed);
            var delete = await (Task<Result>)GeneratorTestHost.RunTransactional(
                db, txType, (ctx, tx) => { ((dynamic)tx).Account.Delete(1); return Result.Ok(); },
                PropagationMode.Confirmed);
            Assert.That(delete.IsOk(), Is.True);
        }

        using var reopenedCold = ColdStore.Open(dir).Unwrap();
        var reopenedDb = Activator.CreateInstance(dbType, reopenedCold)!;

        var loaded = true;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            reopenedDb, txType, (ctx, tx) => { loaded = ((dynamic)tx).Account.Storage.Load(1).IsOk(); return Result.Ok(); },
            PropagationMode.Optimistic);

        Assert.That(loaded, Is.False, "A deleted row's cold copy must genuinely be gone after reopening, not just absent in the closed process' memory.");
    }

    // ---- Decision 3: Insert over a cold-only key overwrites, doesn't reject ----

    [Test]
    public async Task Insert_OverAKeyThatExistsColdOnly_SucceedsAndOverwritesTheColdCopy() {
        using var cold = ColdStore.Open(dir).Unwrap();
        var (db, txType, asm) = NewDb(cold);

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Account.Insert((dynamic)NewAccount(asm, 1, 1, 100m)); return Result.Ok(); },
            PropagationMode.Confirmed);
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Account.Evict(1); return Result.Ok(); },
            PropagationMode.Optimistic);

        var goneFromMemory = true;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { goneFromMemory = !(bool)GeneratorTestHost.InvokeHelper(asm, "TestNs.TestHelpers", "AccountFindIsOk", (object)((dynamic)tx).Account, 1)!; return Result.Ok(); },
            PropagationMode.Optimistic);
        Assert.That(goneFromMemory, Is.True, "Sanity: genuinely gone from memory now, only reachable through Load/Peek.");

        var insert = await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Account.Insert((dynamic)NewAccount(asm, 1, 1, 999m)); return Result.Ok(); },
            PropagationMode.Confirmed);
        Assert.That(insert.IsOk(), Is.True, "Insert over a cold-only key must succeed, not reject as a duplicate (decision 3).");

        decimal balance = -1;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                dtx.Account.Storage.Load(1);
                balance = (decimal)GeneratorTestHost.InvokeHelper(asm, "TestNs.TestHelpers", "AccountBalance", (object)dtx.Account, 1)!;
                return Result.Ok();
            }, PropagationMode.Optimistic);
        Assert.That(balance, Is.EqualTo(999m), "The durable copy must be the overwritten value, not the original evicted one.");
    }

    // ---- .Storage edge cases ----

    [Test]
    public async Task StorageLoad_AGenuinelyAbsentKey_ReturnsIndexKeyNotFound() {
        using var cold = ColdStore.Open(dir).Unwrap();
        var (db, txType, _) = NewDb(cold);

        Result loadResult = default!;
        var result = await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { loadResult = ((dynamic)tx).Account.Storage.Load(999); return Result.Ok(); },
            PropagationMode.Optimistic);

        Assert.That(result.IsOk(), Is.True);
        Assert.That(loadResult.IsError(), Is.True);
        Assert.That(loadResult.GetError().Kind, Is.EqualTo(ErrorKind.IndexKeyNotFound));
    }

    [Test]
    public async Task StorageEvict_ARowNotCurrentlyLoaded_IsAnIdempotentNoOp() {
        using var cold = ColdStore.Open(dir).Unwrap();
        var (db, txType, _) = NewDb(cold);

        Result evictResult = default!;
        var result = await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { evictResult = ((dynamic)tx).Account.Evict(999); return Result.Ok(); },
            PropagationMode.Optimistic);

        Assert.That(result.IsOk(), Is.True);
        Assert.That(evictResult.IsOk(), Is.True);
    }

    [Test]
    public async Task StoragePeek_AGenuinelyAbsentKey_ReturnsIndexKeyNotFound() {
        using var cold = ColdStore.Open(dir).Unwrap();
        var (db, txType, _) = NewDb(cold);

        dynamic peekResult = null!;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { peekResult = ((dynamic)tx).Account.Storage.Peek(999); return Result.Ok(); },
            PropagationMode.Optimistic);

        Assert.That((bool)peekResult.IsError(), Is.True);
        Assert.That((ErrorKind)peekResult.GetError().Kind, Is.EqualTo(ErrorKind.IndexKeyNotFound));
    }

    [Test]
    public async Task StoragePeek_CalledWhileARunIsInFlight_CompletesPromptlyInsteadOfQueuingBehindIt() {
        // Confirms .Storage.Peek genuinely bypasses DbContext.Run through the
        // generated path too - cold.Peek opens its own independent read-only
        // txn (see ColdStore.Peek), never touching ActiveWriteTxn/IsScopeActive,
        // so it must complete even while a separate Run call is blocked mid-flight.
        using var cold = ColdStore.Open(dir).Unwrap();
        var (db, txType, asm) = NewDb(cold);

        object accountsOps = null!;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                dtx.Account.Insert((dynamic)NewAccount(asm, 1, 1, 100m));
                accountsOps = dtx.Account;
                return Result.Ok();
            }, PropagationMode.Confirmed);

        var runEntered = new TaskCompletionSource();
        var releaseRun = new TaskCompletionSource();
        var blockingRun = (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                runEntered.SetResult();
                releaseRun.Task.Wait();
                return Result.Ok();
            }, PropagationMode.Optimistic);
        await runEntered.Task;

        var peekTask = Task.Run(() => ((dynamic)accountsOps).Storage.Peek(1));
        var completed = await Task.WhenAny((Task)peekTask, Task.Delay(TimeSpan.FromSeconds(5)));

        releaseRun.SetResult();
        await blockingRun;

        Assert.That(completed, Is.SameAs(peekTask), "Storage.Peek did not complete while a Run call was still in flight - it appears to be queued behind the single writer.");
    }

    // ---- Async durability sync: the writer thread must not stall on Confirmed's fsync ----

    [Test]
    public async Task TwoConfirmedInsertsFiredWithoutAwaitingTheFirst_BothDurablySurviveReopen() {
        System.Reflection.Assembly asm;
        Type dbType, txType;
        using (var cold = ColdStore.Open(dir).Unwrap()) {
            (asm, _) = GeneratorTestHost.CompileAndLoad(Source);
            dbType = asm.GetType("TestNs.VaultDb")!;
            txType = asm.GetType("TestNs.VaultDbTransaction")!;
            var db = Activator.CreateInstance(dbType, cold)!;

            var first = (Task<Result>)GeneratorTestHost.RunTransactional(
                db, txType, (ctx, tx) => { ((dynamic)tx).Account.Insert((dynamic)NewAccount(asm, 1, 1, 100m)); return Result.Ok(); },
                PropagationMode.Confirmed);
            var second = (Task<Result>)GeneratorTestHost.RunTransactional(
                db, txType, (ctx, tx) => { ((dynamic)tx).Account.Insert((dynamic)NewAccount(asm, 2, 1, 100m)); return Result.Ok(); },
                PropagationMode.Confirmed);
            var results = await Task.WhenAll(first, second);

            Assert.That(results[0].IsOk(), Is.True);
            Assert.That(results[1].IsOk(), Is.True);
        }

        using var reopenedCold = ColdStore.Open(dir).Unwrap();
        var reopenedDb = Activator.CreateInstance(dbType, reopenedCold)!;
        reopenedCold.CompleteRecovery();

        bool firstLoaded = false, secondLoaded = false;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            reopenedDb, txType, (ctx, tx) => {
                dynamic dtx = tx;
                firstLoaded = dtx.Account.Storage.Load(1).IsOk();
                secondLoaded = dtx.Account.Storage.Load(2).IsOk();
                return Result.Ok();
            }, PropagationMode.Optimistic);

        Assert.That(firstLoaded, Is.True);
        Assert.That(secondLoaded, Is.True);
    }

    [Test]
    public async Task MixingConfirmedAndOptimisticCalls_AllCompleteWithinAShortWindow() {
        using var cold = ColdStore.Open(dir).Unwrap();
        var (db, txType, asm) = NewDb(cold);

        var confirmed = (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Account.Insert((dynamic)NewAccount(asm, 1, 1, 100m)); return Result.Ok(); },
            PropagationMode.Confirmed);
        var optimisticTasks = Enumerable.Range(2, 20)
            .Select(id => (Task<Result>)GeneratorTestHost.RunTransactional(
                db, txType, (ctx, tx) => { ((dynamic)tx).Account.Insert((dynamic)NewAccount(asm, id, 1, 100m)); return Result.Ok(); },
                PropagationMode.Optimistic))
            .ToArray();

        var all = Task.WhenAll(optimisticTasks.Cast<Task>().Append(confirmed));
        var completed = await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.That(completed, Is.SameAs(all), "Mixed Confirmed/Optimistic calls did not all complete promptly - the writer thread may be stalling on the sync call.");
        Assert.That(confirmed.Result.IsOk(), Is.True);
        Assert.That(optimisticTasks, Has.All.Matches<Task<Result>>(t => t.Result.IsOk()));
    }

    // ---- Eviction write-through (Docs/05-wal-design.md Phase 3) ----
    // Evict stages a candidate; the batched mdbx write-through + memory drop happen at the
    // NEXT operation boundary (ColdStore.BeginScope -> ApplyPendingEvictions). These tests
    // pin the invariant that makes Load/Peek safe: an evicted row's mdbx copy is exactly
    // current before the memory copy goes - including when the row changes between staging
    // and apply.

    [Test]
    public async Task Evict_ThenNextOperation_RowIsInColdStorageAndDroppedFromMemory() {
        using var cold = ColdStore.Open(dir).Unwrap();
        var (db, txType, asm) = NewDb(cold);

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Account.Insert((dynamic)NewAccount(asm, 1, 1, 100m)); return Result.Ok(); },
            PropagationMode.Confirmed);

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Account.Evict(1); return Result.Ok(); },
            PropagationMode.Optimistic);

        bool inMemory = true, peeked = false, reloaded = false;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                inMemory = (bool)GeneratorTestHost.InvokeHelper(asm, "TestNs.TestHelpers", "AccountFindIsOk", (object)dtx.Account, 1)!;
                var peek = dtx.Account.Storage.Peek(1);
                peeked = peek.IsOk() && peek.Unwrap().Balance == 100m;
                reloaded = dtx.Account.Storage.Load(1).IsOk();
                return Result.Ok();
            }, PropagationMode.Optimistic);

        Assert.That(inMemory, Is.False, "The eviction batch applied at this operation's boundary - the row must be gone from memory.");
        Assert.That(peeked, Is.True, "The evicted row's mdbx copy must be exactly current - Peek reads it back with the original value.");
        Assert.That(reloaded, Is.True, "Load must bring the evicted row back into memory from cold storage.");
    }

    [Test]
    public async Task Evict_ThenUpdateBeforeTheBoundary_TheUpdatedValueIsWhatGetsPersisted() {
        using var cold = ColdStore.Open(dir).Unwrap();
        var (db, txType, asm) = NewDb(cold);

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Account.Insert((dynamic)NewAccount(asm, 1, 1, 100m)); return Result.Ok(); },
            PropagationMode.Confirmed);

        // Evict stages the candidate, then the row is updated BEFORE the batch applies -
        // the batch must re-read and persist the CURRENT value (500), not the stale one (100).
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                dtx.Account.Evict(1);
                dtx.Account.Update(1, (dynamic)NewAccount(asm, 1, 1, 500m));
                return Result.Ok();
            }, PropagationMode.Optimistic);

        decimal persisted = 0m, reloaded = 0m;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                var peek = dtx.Account.Storage.Peek(1);
                persisted = peek.IsOk() ? peek.Unwrap().Balance : -1m;
                if (dtx.Account.Storage.Load(1).IsOk()) {
                    reloaded = (decimal)GeneratorTestHost.InvokeHelper(asm, "TestNs.TestHelpers", "AccountBalance", (object)dtx.Account, 1)!;
                }
                return Result.Ok();
            }, PropagationMode.Optimistic);

        Assert.That(persisted, Is.EqualTo(500m),
            "The batch applied at this boundary must have written the row's current (updated) value, not the value staged at Evict time.");
        Assert.That(reloaded, Is.EqualTo(500m),
            "The update must not be lost: Load brings the evicted-but-current row back, with the post-update value.");
    }

    [Test]
    public async Task Evict_ThenDeleteBeforeTheBoundary_AfterRestart_TheRowStaysDeleted() {
        System.Reflection.Assembly asm;
        Type dbType, txType;
        using (var cold = ColdStore.Open(dir).Unwrap()) {
            (asm, _) = GeneratorTestHost.CompileAndLoad(Source);
            dbType = asm.GetType("TestNs.VaultDb")!;
            txType = asm.GetType("TestNs.VaultDbTransaction")!;
            var db = Activator.CreateInstance(dbType, cold)!;

            await (Task<Result>)GeneratorTestHost.RunTransactional(
                db, txType, (ctx, tx) => { ((dynamic)tx).Account.Insert((dynamic)NewAccount(asm, 1, 1, 100m)); return Result.Ok(); },
                PropagationMode.Confirmed);
            await (Task<Result>)GeneratorTestHost.RunTransactional(
                db, txType, (ctx, tx) => {
                    dynamic dtx = tx;
                    dtx.Account.Evict(1);
                    dtx.Account.Delete(1);
                    return Result.Ok();
                }, PropagationMode.Confirmed);
            // Boundary op: applies the eviction batch - the candidate must be skipped
            // (row already deleted), never re-Put into cold storage.
            await (Task<Result>)GeneratorTestHost.RunTransactional(
                db, txType, (ctx, tx) => Result.Ok(), PropagationMode.Optimistic);
        }

        using var reopenedCold = ColdStore.Open(dir).Unwrap();
        var reopenedDb = Activator.CreateInstance(dbType, reopenedCold)!;
        reopenedCold.CompleteRecovery();

        var found = true;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            reopenedDb, txType, (ctx, tx) => { found = (bool)GeneratorTestHost.InvokeHelper(asm, "TestNs.TestHelpers", "AccountFindIsOk", (object)((dynamic)tx).Account, 1)!; return Result.Ok(); },
            PropagationMode.Optimistic);

        Assert.That(found, Is.False,
            "A delete after an eviction staging must win: the batch apply must skip the gone row, and recovery must not resurrect it.");
    }

    [Test]
    public async Task Evict_ThenCloseAndReopen_TheRowSurvivesRestart() {
        System.Reflection.Assembly asm;
        Type dbType, txType;
        using (var cold = ColdStore.Open(dir).Unwrap()) {
            (asm, _) = GeneratorTestHost.CompileAndLoad(Source);
            dbType = asm.GetType("TestNs.VaultDb")!;
            txType = asm.GetType("TestNs.VaultDbTransaction")!;
            var db = Activator.CreateInstance(dbType, cold)!;

            await (Task<Result>)GeneratorTestHost.RunTransactional(
                db, txType, (ctx, tx) => { ((dynamic)tx).Account.Insert((dynamic)NewAccount(asm, 1, 1, 100m)); return Result.Ok(); },
                PropagationMode.Confirmed);
            await (Task<Result>)GeneratorTestHost.RunTransactional(
                db, txType, (ctx, tx) => { ((dynamic)tx).Account.Evict(1); return Result.Ok(); },
                PropagationMode.Optimistic);
            await (Task<Result>)GeneratorTestHost.RunTransactional(
                db, txType, (ctx, tx) => Result.Ok(), PropagationMode.Optimistic);
        }

        using var reopenedCold = ColdStore.Open(dir).Unwrap();
        var reopenedDb = Activator.CreateInstance(dbType, reopenedCold)!;
        reopenedCold.CompleteRecovery();

        bool loaded = false, found = false;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            reopenedDb, txType, (ctx, tx) => {
                dynamic dtx = tx;
                loaded = dtx.Account.Storage.Load(1).IsOk();
                found = (bool)GeneratorTestHost.InvokeHelper(asm, "TestNs.TestHelpers", "AccountFindIsOk", (object)dtx.Account, 1)!;
                return Result.Ok();
            }, PropagationMode.Optimistic);

        Assert.That(loaded, Is.True, "An evicted row must survive a restart - its write-through copy is in cold storage.");
        Assert.That(found, Is.True);
    }

    [Test]
    public async Task Evict_CrossingTheSizeThreshold_FlushesImmediatelyWithinTheSameOperation() {
        using var cold = ColdStore.Open(dir, evictionBatchThresholdBytes: 1).Unwrap();
        var (db, txType, asm) = NewDb(cold);

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Account.Insert((dynamic)NewAccount(asm, 1, 1, 100m)); return Result.Ok(); },
            PropagationMode.Confirmed);

        var droppedWithinSameOperation = false;
        var persisted = -1m;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                dtx.Account.Evict(1);
                // With a 1-byte threshold, staging this single eviction already crosses
                // it - the batch must be applied (mdbx write-through + memory drop)
                // before this same operation returns, not deferred to the next
                // operation's BeginScope boundary.
                droppedWithinSameOperation = !(bool)GeneratorTestHost.InvokeHelper(asm, "TestNs.TestHelpers", "AccountFindIsOk", (object)dtx.Account, 1)!;
                var peek = dtx.Account.Storage.Peek(1);
                persisted = peek.IsOk() ? peek.Unwrap().Balance : -1m;
                return Result.Ok();
            }, PropagationMode.Optimistic);

        Assert.That(droppedWithinSameOperation, Is.True,
            "Crossing the eviction batch's size threshold must flush immediately, not wait for the next operation boundary.");
        Assert.That(persisted, Is.EqualTo(100m));
    }

    // ---- Finding 3: a Confirmed fsync failure poisons the database ----

#if DEBUG
    [Test]
    public async Task Confirmed_WhenTheWalFsyncFails_ReturnsTheFsyncErrorAndPoisonsTheDatabase() {
        using var cold = ColdStore.Open(dir).Unwrap();
        var (db, txType, asm) = NewDb(cold);

        var insert = await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Account.Insert((dynamic)NewAccount(asm, 1, 1, 100m)); return Result.Ok(); },
            PropagationMode.Confirmed);
        Assert.That(insert.IsOk(), Is.True);

        cold.Wal.TestOnlyBeforeFlush = () => throw new IOException("injected fsync failure");

        var confirmed = await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Account.Update(1, (dynamic)NewAccount(asm, 1, 1, 500m)); return Result.Ok(); },
            PropagationMode.Confirmed);

        Assert.That(confirmed.IsError(), Is.True);
        Assert.That(confirmed.GetError().Kind, Is.EqualTo(ErrorKind.WalDurabilityFailed),
            "A failed fsync is a durability failure, not a generic system failure. It used to be" +
            "classified as SystemFailure, and the caller-visible kind was masked by a hard-coded" +
            "WalDurabilityFailed in the poison gate; now the real error is returned, so the kind" +
            "has to be right - and this test is what pins it.");
    }
#endif

#if DEBUG
    [Test]
    public async Task AfterAConfirmedFsyncFailure_EverySubsequentOperationIsRefusedUntilRestart() {
        using var cold = ColdStore.Open(dir).Unwrap();
        var (db, txType, asm) = NewDb(cold);

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Account.Insert((dynamic)NewAccount(asm, 1, 1, 100m)); return Result.Ok(); },
            PropagationMode.Confirmed);

        cold.Wal.TestOnlyBeforeFlush = () => throw new IOException("injected fsync failure");

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Account.Update(1, (dynamic)NewAccount(asm, 1, 1, 500m)); return Result.Ok(); },
            PropagationMode.Confirmed);

        var optimistic = await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Account.Update(1, (dynamic)NewAccount(asm, 1, 1, 900m)); return Result.Ok(); },
            PropagationMode.Optimistic);
        var confirmed = await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Account.Update(1, (dynamic)NewAccount(asm, 1, 1, 900m)); return Result.Ok(); },
            PropagationMode.Confirmed);

        Assert.That(optimistic.IsError(), Is.True);
        Assert.That(optimistic.GetError().Kind, Is.EqualTo(ErrorKind.WalDurabilityFailed));
        Assert.That(confirmed.IsError(), Is.True);
        Assert.That(confirmed.GetError().Kind, Is.EqualTo(ErrorKind.WalDurabilityFailed));
    }
#endif
}
