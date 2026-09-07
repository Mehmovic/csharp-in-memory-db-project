using RhinoDB.Core;
using RhinoDB.Core.Exceptions;
using RhinoDB.Lib.Execution;
using RhinoDB.Lib.Indexing;

namespace RhinoDB.Lib.Cold.Test;

// Proves the ambient-txn wiring end to end: DbExecutionLoop must call
// ColdStore.BeginScope() before invoking an operation and EndScope(...) after, per
// Docs/02-architecture.md "Cold storage" and this stage's decisions 1-5,7. Every
// test here drives PersistentTable through a real DbContext.Run - not a hand-
// simulated BeginScope/EndScope bracket - because proving the wiring itself (not
// just PersistentTable's own logic in isolation) is this suite's job. Account is
// the shared Stage 5 fixture defined in MemoryPackRoundTripTests.cs.
public class PersistentTableTests {
    private string dir = "";

    [SetUp]
    public void SetUp() {
        dir = Path.Combine(Path.GetTempPath(), "rhinodb-persistenttable-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
    }

    [TearDown]
    public void TearDown() {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    static private PersistentTable<int, Account> NewTable(ColdStore cold, string subDb = "accounts") =>
        new(cold, subDb, chunkSize: 4, new HashIndex<int>(), static a => a.Id);

    static private readonly Account Alice = new(1, "alice@example.com", 100m);

    // ---- Insert / Get, in-process ----

    [Test]
    public async Task Insert_ThenGet_ReturnsTheInsertedRow() {
        using var cold = ColdStore.Open(dir).Unwrap();
        var table = NewTable(cold);
        var ctx = new DbContext(cold);

        var insert = await ctx.Run(_ => table.Insert(Alice));
        var get = await ctx.Run(_ => table.Get(1));

        Assert.That(insert.IsOk(), Is.True);
        Assert.That(get.Unwrap(), Is.EqualTo(Alice));
    }

    [Test]
    public async Task Update_AttemptingToChangePrimaryKey_ReturnsFailureWithPrimaryKeyImmutableException() {
        using var cold = ColdStore.Open(dir).Unwrap();
        var table = NewTable(cold);
        var ctx = new DbContext(cold);
        await ctx.Run(_ => table.Insert(Alice));

        var result = await ctx.Run(_ => table.Update(1, Alice with { Id = 2 }));

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetError().ToException(), Is.InstanceOf<PrimaryKeyImmutableException>());
    }

    [Test]
    public async Task Get_ForARowThatOnlyExistsColdAndWasNeverLoaded_ReturnsNotFound() {
        // Decision 1: Get never touches cold storage. A fresh context/table pair
        // against the same directory has an empty in-memory Table even though the
        // row is durably present - Get must not silently reach for it.
        using (var cold = ColdStore.Open(dir).Unwrap()) {
            var table = NewTable(cold);
            var ctx = new DbContext(cold);
            await ctx.RunConfirmed(_ => table.Insert(Alice));
        }

        using var reopenedCold = ColdStore.Open(dir).Unwrap();
        var reopenedTable = NewTable(reopenedCold);
        var reopenedCtx = new DbContext(reopenedCold);

        var get = await reopenedCtx.Run(_ => reopenedTable.Get(1));

        Assert.That(get.IsError(), Is.True);
        Assert.That(get.GetError().ToException(), Is.InstanceOf<IndexKeyNotFoundException>());
    }

    [Test]
    public async Task Update_ForARowThatOnlyExistsColdAndWasNeverLoaded_ReturnsNotFound() {
        // Decision 4: no implicit load-then-update. From the in-memory Table's own
        // perspective this is indistinguishable from a genuinely unknown key.
        using (var cold = ColdStore.Open(dir).Unwrap()) {
            var table = NewTable(cold);
            var ctx = new DbContext(cold);
            await ctx.RunConfirmed(_ => table.Insert(Alice));
        }

        using var reopenedCold = ColdStore.Open(dir).Unwrap();
        var reopenedTable = NewTable(reopenedCold);
        var reopenedCtx = new DbContext(reopenedCold);

        var update = await reopenedCtx.Run(_ => reopenedTable.Update(1, Alice with { Balance = 500m }));

        Assert.That(update.IsError(), Is.True);
        Assert.That(update.GetError().ToException(), Is.InstanceOf<IndexKeyNotFoundException>());
    }

    // ---- The core durability promise: survives a real close + reopen ----

    [Test]
    public async Task Insert_Optimistic_ThenCloseAndReopen_LoadRoundTripsTheRow() {
        using (var cold = ColdStore.Open(dir).Unwrap()) {
            var table = NewTable(cold);
            var ctx = new DbContext(cold);
            var insert = await ctx.Run(_ => table.Insert(Alice), PropagationMode.Optimistic);
            Assert.That(insert.IsOk(), Is.True);
        }

        using var reopenedCold = ColdStore.Open(dir).Unwrap();
        var reopenedTable = NewTable(reopenedCold);
        var reopenedCtx = new DbContext(reopenedCold);

        var load = await reopenedCtx.Run(_ => reopenedTable.Load(1));
        var get = await reopenedCtx.Run(_ => reopenedTable.Get(1));

        Assert.That(load.IsOk(), Is.True);
        Assert.That(get.Unwrap(), Is.EqualTo(Alice));
    }

    [Test]
    public async Task Insert_Confirmed_ThenCloseAndReopen_LoadRoundTripsTheRow() {
        using (var cold = ColdStore.Open(dir).Unwrap()) {
            var table = NewTable(cold);
            var ctx = new DbContext(cold);
            var insert = await ctx.RunConfirmed(_ => table.Insert(Alice));
            Assert.That(insert.IsOk(), Is.True);
        }

        using var reopenedCold = ColdStore.Open(dir).Unwrap();
        var reopenedTable = NewTable(reopenedCold);
        var reopenedCtx = new DbContext(reopenedCold);

        var load = await reopenedCtx.Run(_ => reopenedTable.Load(1));
        var get = await reopenedCtx.Run(_ => reopenedTable.Get(1));

        Assert.That(load.IsOk(), Is.True);
        Assert.That(get.Unwrap(), Is.EqualTo(Alice));
    }

    [Test]
    public async Task Update_ThenCloseAndReopen_LoadReflectsTheUpdatedValueNotTheOriginal() {
        using (var cold = ColdStore.Open(dir).Unwrap()) {
            var table = NewTable(cold);
            var ctx = new DbContext(cold);
            await ctx.RunConfirmed(_ => table.Insert(Alice));
            await ctx.RunConfirmed(_ => table.Update(1, Alice with { Balance = 250m }));
        }

        using var reopenedCold = ColdStore.Open(dir).Unwrap();
        var reopenedTable = NewTable(reopenedCold);
        var reopenedCtx = new DbContext(reopenedCold);
        await reopenedCtx.Run(_ => reopenedTable.Load(1));

        var get = await reopenedCtx.Run(_ => reopenedTable.Get(1));

        Assert.That(get.Unwrap().Balance, Is.EqualTo(250m));
    }

    [Test]
    public async Task Delete_ThenCloseAndReopen_LoadReturnsNotFound() {
        using (var cold = ColdStore.Open(dir).Unwrap()) {
            var table = NewTable(cold);
            var ctx = new DbContext(cold);
            await ctx.RunConfirmed(_ => table.Insert(Alice));
            var delete = await ctx.RunConfirmed(_ => table.Delete(1));
            Assert.That(delete.IsOk(), Is.True);
        }

        using var reopenedCold = ColdStore.Open(dir).Unwrap();
        var reopenedTable = NewTable(reopenedCold);
        var reopenedCtx = new DbContext(reopenedCold);

        var load = await reopenedCtx.Run(_ => reopenedTable.Load(1));

        Assert.That(load.IsError(), Is.True);
        Assert.That(load.GetError().ToException(), Is.InstanceOf<IndexKeyNotFoundException>());
    }

    [Test]
    public async Task Delete_ARowThatOnlyExistsColdAndWasNeverLoaded_StillRemovesTheColdCopy() {
        // Delete's fallthrough path (see this plan's Part C "Delete" algorithm):
        // the in-memory Delete misses (not loaded), but the key still exists cold,
        // so cold.Del must still run rather than reporting not-found.
        using (var cold = ColdStore.Open(dir).Unwrap()) {
            var table = NewTable(cold);
            var ctx = new DbContext(cold);
            await ctx.RunConfirmed(_ => table.Insert(Alice));
        }

        using var reopenedCold = ColdStore.Open(dir).Unwrap();
        var reopenedTable = NewTable(reopenedCold);
        var reopenedCtx = new DbContext(reopenedCold);

        var delete = await reopenedCtx.RunConfirmed(_ => reopenedTable.Delete(1));
        Assert.That(delete.IsOk(), Is.True);

        var load = await reopenedCtx.Run(_ => reopenedTable.Load(1));
        Assert.That(load.IsError(), Is.True);
    }

    // ---- Async durability sync (2026-09-07 addendum): the writer thread must not
    // ---- stall on Confirmed's fsync, but the caller must still durably wait for it ----

    [Test]
    public async Task TwoConfirmedInsertsFiredWithoutAwaitingTheFirst_BothDurablySurviveReopen() {
        // Exercises ColdStore's pendingSync chaining directly - a sequential
        // await-then-await pattern (every other test in this file) never has two
        // syncs in flight against each other at once, so it can't catch a chaining
        // bug on its own.
        using (var cold = ColdStore.Open(dir).Unwrap()) {
            var table = NewTable(cold);
            var ctx = new DbContext(cold);

            var first = ctx.RunConfirmed(_ => table.Insert(Alice));
            var second = ctx.RunConfirmed(_ => table.Insert(Alice with { Id = 2 }));
            var results = await Task.WhenAll(first, second);

            Assert.That(results[0].IsOk(), Is.True);
            Assert.That(results[1].IsOk(), Is.True);
        }

        using var reopenedCold = ColdStore.Open(dir).Unwrap();
        var reopenedTable = NewTable(reopenedCold);
        var reopenedCtx = new DbContext(reopenedCold);

        Assert.That((await reopenedCtx.Run(_ => reopenedTable.Load(1))).IsOk(), Is.True);
        Assert.That((await reopenedCtx.Run(_ => reopenedTable.Load(2))).IsOk(), Is.True);
    }

    [Test]
    public async Task MixingConfirmedAndOptimisticCalls_AllCompleteWithinAShortWindow() {
        // Coarse deadlock/regression guard, not a precision throughput claim (real
        // fsync timing is machine-dependent, so this deliberately doesn't assert an
        // ordering between the Confirmed and Optimistic completions - only that
        // firing a Confirmed call without awaiting it first can never make later,
        // unrelated Optimistic calls wait anywhere close to as long as a suite-wide
        // timeout. The actual throughput payoff is measured for real in Part H's
        // benchmark project, not asserted here.
        using var cold = ColdStore.Open(dir).Unwrap();
        var table = NewTable(cold);
        var ctx = new DbContext(cold);

        var confirmed = ctx.RunConfirmed(_ => table.Insert(Alice));
        var optimisticTasks = Enumerable.Range(2, 20)
            .Select(id => ctx.Run(_ => table.Insert(Alice with { Id = id })))
            .ToArray();

        var all = Task.WhenAll(optimisticTasks.Cast<Task>().Append(confirmed));
        var completed = await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.That(completed, Is.SameAs(all), "Mixed Confirmed/Optimistic calls did not all complete promptly - the writer thread may be stalling on the sync call.");
        Assert.That(confirmed.Result.IsOk(), Is.True);
        Assert.That(optimisticTasks, Has.All.Matches<Task<Result>>(t => t.Result.IsOk()));
    }

    // ---- Decision 3: Insert over a cold-only key overwrites, doesn't reject ----

    [Test]
    public async Task Insert_OverAKeyThatExistsColdOnly_SucceedsAndOverwritesTheColdCopy() {
        using var cold = ColdStore.Open(dir).Unwrap();
        var table = NewTable(cold);
        var ctx = new DbContext(cold);
        await ctx.RunConfirmed(_ => table.Insert(Alice));
        var evict = await ctx.Run(_ => table.Evict(1));
        Assert.That(evict.IsOk(), Is.True);
        // Sanity: genuinely gone from memory now, only reachable through Load/cold.
        Assert.That((await ctx.Run(_ => table.Get(1))).IsError(), Is.True);

        var overwritten = Alice with { Balance = 999m };
        var insert = await ctx.RunConfirmed(_ => table.Insert(overwritten));

        Assert.That(insert.IsOk(), Is.True, "Insert over a cold-only key must succeed, not reject as a duplicate (decision 3).");
        var load = await ctx.Run(_ => table.Load(1));
        Assert.That(load.IsOk(), Is.True);
        Assert.That((await ctx.Run(_ => table.Get(1))).Unwrap(), Is.EqualTo(overwritten),
            "The durable copy must be the overwritten value, not the original evicted one.");
    }

    // ---- Decision 7: writes require an active Run scope ----

    [Test]
    public void Insert_CalledDirectlyOutsideOfAnyRunScope_ReturnsNoActiveTransaction() {
        using var cold = ColdStore.Open(dir).Unwrap();
        var table = NewTable(cold);
        // Deliberately no DbContext.Run anywhere - cold.IsScopeActive is false.

        var result = table.Insert(Alice);

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetError().Kind, Is.EqualTo(ErrorKind.NoActiveTransaction));
    }

    [Test]
    public void Insert_CalledDirectlyOutsideOfAnyRunScope_DoesNotMutateInMemoryOrColdState() {
        using var cold = ColdStore.Open(dir).Unwrap();
        var table = NewTable(cold);

        table.Insert(Alice);

        Assert.That(table.Count, Is.EqualTo(0));
    }

    // ---- Overall-failure aborts the cold txn (documented known limitation) ----

    [Test]
    public async Task Run_WhenTheOperationReturnsErrorAfterAPersistentInsertSucceeded_AbortsTheColdTransaction() {
        using (var cold = ColdStore.Open(dir).Unwrap()) {
            var table = NewTable(cold);
            var ctx = new DbContext(cold);

            var result = await ctx.RunConfirmed(_ => {
                var insert = table.Insert(Alice);
                if (insert.IsError()) return insert;
                return Result.Error(DbError.DuplicateKey()); // unrelated failure - forces overall Run to error
            });

            Assert.That(result.IsError(), Is.True);
            // Known limitation (Part C): the in-memory mutation from earlier in the
            // same Run is NOT rolled back - this is what makes it a genuine gap, not
            // just a cold-storage-only concern.
            Assert.That(table.Get(1).IsOk(), Is.True, "In-memory Insert is not undone within the same Run - documented, not accidental.");
        }

        // But the cold txn must have been aborted, not committed - a fresh context
        // against the same directory must not see the row at all.
        using var reopenedCold = ColdStore.Open(dir).Unwrap();
        var reopenedTable = NewTable(reopenedCold);
        var reopenedCtx = new DbContext(reopenedCold);

        var load = await reopenedCtx.Run(_ => reopenedTable.Load(1));

        Assert.That(load.IsError(), Is.True, "The cold txn for a Run that ultimately returns Error must be aborted, not committed.");
    }

    // ---- Zero regression: a ColdStore-less DbContext behaves exactly as before ----

    [Test]
    public async Task DbContext_WithNoColdStore_StillWorksExactlyAsInStage4() {
        var ctx = new DbContext();

        var result = await ctx.Run(c => Result.Ok(42));

        Assert.That(result.IsOk(), Is.True);
        Assert.That(result.Unwrap(), Is.EqualTo(42));
    }
}
