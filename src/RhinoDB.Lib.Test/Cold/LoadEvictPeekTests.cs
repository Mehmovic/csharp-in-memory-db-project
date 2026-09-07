using RhinoDB.Core;
using RhinoDB.Core.Exceptions;
using RhinoDB.Lib.Execution;
using RhinoDB.Lib.Indexing;

namespace RhinoDB.Lib.Cold.Test;

// Dedicated, exhaustive suite for Load/Evict/Peek (Docs/02-architecture.md "Cold
// storage", decision 2 in the Stage 5 plan) - PersistentTableTests.cs already
// exercises Load/Evict incidentally as part of proving the write-through/durability
// wiring; this suite is the one that pins their edge cases directly. Account/the
// PersistentTable helper pattern mirror PersistentTableTests.cs.
public class LoadEvictPeekTests {
    private string dir = "";

    [SetUp]
    public void SetUp() {
        dir = Path.Combine(Path.GetTempPath(), "rhinodb-loadevictpeek-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
    }

    [TearDown]
    public void TearDown() {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    static private PersistentTable<int, Account> NewTable(ColdStore cold, string subDb = "accounts") =>
        new(cold, subDb, chunkSize: 4, new HashIndex<int>(), static a => a.Id);

    static private readonly Account Alice = new(1, "alice@example.com", 100m);

    // ---- Load ----

    [Test]
    public async Task Load_ARowThatExistsColdButIsNotInMemory_MakesItAppearInGet() {
        using var cold = ColdStore.Open(dir).Unwrap();
        var table = NewTable(cold);
        var ctx = new DbContext(cold);
        await ctx.RunConfirmed(_ => table.Insert(Alice));
        await ctx.Run(_ => table.Evict(1)); // now cold-only

        var load = await ctx.Run(_ => table.Load(1));
        var get = await ctx.Run(_ => table.Get(1));

        Assert.That(load.IsOk(), Is.True);
        Assert.That(get.Unwrap(), Is.EqualTo(Alice));
    }

    [Test]
    public async Task Load_ARowAlreadyInMemory_IsAnIdempotentNoOp() {
        using var cold = ColdStore.Open(dir).Unwrap();
        var table = NewTable(cold);
        var ctx = new DbContext(cold);
        await ctx.RunConfirmed(_ => table.Insert(Alice));

        var load = await ctx.Run(_ => table.Load(1));

        Assert.That(load.IsOk(), Is.True);
        Assert.That((await ctx.Run(_ => table.Get(1))).Unwrap(), Is.EqualTo(Alice));
        Assert.That(table.Count, Is.EqualTo(1)); // no duplicate row created
    }

    [Test]
    public async Task Load_AGenuinelyAbsentKey_ReturnsIndexKeyNotFound() {
        using var cold = ColdStore.Open(dir).Unwrap();
        var table = NewTable(cold);
        var ctx = new DbContext(cold);

        var load = await ctx.Run(_ => table.Load(999));

        Assert.That(load.IsError(), Is.True);
        Assert.That(load.GetError().ToException(), Is.InstanceOf<IndexKeyNotFoundException>());
    }

    // ---- Evict ----

    [Test]
    public async Task Evict_ALoadedRow_MakesItDisappearFromGet() {
        using var cold = ColdStore.Open(dir).Unwrap();
        var table = NewTable(cold);
        var ctx = new DbContext(cold);
        await ctx.RunConfirmed(_ => table.Insert(Alice));

        var evict = await ctx.Run(_ => table.Evict(1));

        Assert.That(evict.IsOk(), Is.True);
        Assert.That((await ctx.Run(_ => table.Get(1))).IsError(), Is.True);
    }

    [Test]
    public async Task Evict_ThenReload_RestoresIdenticalData_ProvingEvictNeverTouchedColdStorage() {
        using var cold = ColdStore.Open(dir).Unwrap();
        var table = NewTable(cold);
        var ctx = new DbContext(cold);
        await ctx.RunConfirmed(_ => table.Insert(Alice));

        await ctx.Run(_ => table.Evict(1));
        var reload = await ctx.Run(_ => table.Load(1));

        Assert.That(reload.IsOk(), Is.True);
        Assert.That((await ctx.Run(_ => table.Get(1))).Unwrap(), Is.EqualTo(Alice));
    }

    [Test]
    public async Task Evict_ARowNotCurrentlyLoaded_IsAnIdempotentNoOp() {
        using var cold = ColdStore.Open(dir).Unwrap();
        var table = NewTable(cold);
        var ctx = new DbContext(cold);

        var evict = await ctx.Run(_ => table.Evict(999));

        Assert.That(evict.IsOk(), Is.True);
    }

    // ---- Peek ----

    [Test]
    public async Task Peek_AnEvictedRow_ReturnsTheCorrectDataWithoutLoadingIt() {
        using var cold = ColdStore.Open(dir).Unwrap();
        var table = NewTable(cold);
        var ctx = new DbContext(cold);
        await ctx.RunConfirmed(_ => table.Insert(Alice));
        await ctx.Run(_ => table.Evict(1));

        var peek = table.Peek(1); // synchronous, does not go through DbContext.Run

        Assert.That(peek.IsOk(), Is.True);
        Assert.That(peek.Unwrap(), Is.EqualTo(Alice));
    }

    [Test]
    public async Task Peek_AnEvictedRow_DoesNotCacheItBackIntoMemory() {
        using var cold = ColdStore.Open(dir).Unwrap();
        var table = NewTable(cold);
        var ctx = new DbContext(cold);
        await ctx.RunConfirmed(_ => table.Insert(Alice));
        await ctx.Run(_ => table.Evict(1));

        table.Peek(1);

        Assert.That((await ctx.Run(_ => table.Get(1))).IsError(), Is.True, "Peek must never load a row into memory as a side effect.");
    }

    [Test]
    public void Peek_AGenuinelyAbsentKey_ReturnsIndexKeyNotFound() {
        using var cold = ColdStore.Open(dir).Unwrap();
        var table = NewTable(cold);

        var peek = table.Peek(999);

        Assert.That(peek.IsError(), Is.True);
        Assert.That(peek.GetError().ToException(), Is.InstanceOf<IndexKeyNotFoundException>());
    }

    [Test]
    public async Task Peek_CalledWhileARunIsInFlight_CompletesPromptlyInsteadOfQueuingBehindIt() {
        // Confirms Peek genuinely bypasses DbContext.Run (Docs/02-architecture.md's
        // documented exception to "everything goes through one call point") - if
        // Peek were (incorrectly) funneled through the same single-writer channel,
        // this test would hang until releaseRun is signaled instead of completing
        // well within the timeout.
        using var cold = ColdStore.Open(dir).Unwrap();
        var table = NewTable(cold);
        var ctx = new DbContext(cold);
        await ctx.RunConfirmed(_ => table.Insert(Alice));

        var runEntered = new TaskCompletionSource();
        var releaseRun = new TaskCompletionSource();
        var blockingRun = ctx.Run(_ => {
            runEntered.SetResult();
            releaseRun.Task.Wait();
            return Result.Ok();
        });
        await runEntered.Task;

        var peekTask = Task.Run(() => table.Peek(1));
        var completed = await Task.WhenAny(peekTask, Task.Delay(TimeSpan.FromSeconds(5)));

        releaseRun.SetResult();
        await blockingRun;

        Assert.That(completed, Is.SameAs(peekTask), "Peek did not complete while a Run call was still in flight - it appears to be queued behind the single writer.");
        Assert.That(peekTask.Result.Unwrap(), Is.EqualTo(Alice));
    }
}
