using RhinoDB.Core;

namespace RhinoDB.Lib.Execution.Test;

// Draining means "every accepted operation has finished" - callers close the ColdStore (unmapping libmdbx) right after.
// Each test first runs one operation and waits for it: until the loop has gone idle once, it can still be running on the
// thread that started it, which used to hide the bug (DrainAsync returned the task of StartNew, not of the loop itself).
public class DbExecutionLoopDrainTests {
    [Test]
    public async Task DrainAsync_WaitsForTheOperationInFlight() {
        var db = new DbContext();
        Assert.That((await db.Run(ctx => Result.Ok())).IsOk(), Is.True);
        await Task.Delay(50);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var operation = db.Run(ctx => {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            return Result.Ok();
        }).AsTask();
        Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True, "the operation never started.");

        db.BeginDraining();
        var drain = db.DrainAsync();
        await Task.Delay(200);

        Assert.That(drain.IsCompleted, Is.False, "draining finished while an operation was still running on the loop.");

        release.Set();
        await drain.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That((await operation).IsOk(), Is.True);
    }

    [Test]
    public async Task DrainAsync_AlsoWaitsForOperationsQueuedBehindTheRunningOne() {
        var db = new DbContext();
        Assert.That((await db.Run(ctx => Result.Ok())).IsOk(), Is.True);
        await Task.Delay(50);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var first = db.Run(ctx => {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            return Result.Ok();
        }).AsTask();
        Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True);
        var queued = db.Run(ctx => Result.Ok()).AsTask();

        db.BeginDraining();
        var drain = db.DrainAsync();
        await Task.Delay(200);
        Assert.That(drain.IsCompleted, Is.False, "draining finished with an operation running and another queued.");

        release.Set();
        await drain.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That((await first.WaitAsync(TimeSpan.FromSeconds(5))).IsOk(), Is.True);
        Assert.That((await queued.WaitAsync(TimeSpan.FromSeconds(5))).IsOk(), Is.True, "work accepted before draining began runs - it isn't dropped as DatabaseClosing.");
    }

    [Test]
    public async Task DrainAsync_OnAnIdleLoop_CompletesPromptly() {
        var db = new DbContext();
        Assert.That((await db.Run(ctx => Result.Ok())).IsOk(), Is.True);

        db.BeginDraining();

        await db.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }
}
