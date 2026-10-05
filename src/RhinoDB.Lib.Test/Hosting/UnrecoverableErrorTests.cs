using System.Diagnostics;

using RhinoDB.Core;
using RhinoDB.Lib.Cold;
using RhinoDB.Lib.Execution;
using RhinoDB.Lib.Tables;
using RhinoDB.SchemaContracts;

namespace RhinoDB.Lib.Hosting.Test;

// Fail fast: a poisoned database (a WAL write/fsync failed, a multi-database outcome is unknown, an apply couldn't be
// reverted) can't be recovered in place, so the default is to exit non-zero and let the supervisor restart the engine.
// The exit sequence is driven through the handler's seams (exit / failFast / stderr / timeouts) - nothing here kills
// the test runner; the real process exit is covered out-of-process by ProcessExitTests.
public class UnrecoverableErrorTests {
    private string dir = "";

    [SetUp]
    public void SetUp() {
        dir = Path.Combine(Path.GetTempPath(), "rhinodb-unrecoverable-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
    }

    [TearDown]
    public void TearDown() {
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private sealed class RootDb(ColdStore cold) : DbContext(cold);

    private sealed class ChildDb(ColdStore cold) : DbContext(cold);

    private sealed class TestDb : DbContext;

    static private DbError IoFailure() => DbError.WalDurabilityFailed(new IOException("injected"));

    // ---- the poison itself ----

    [Test]
    public void ConcurrentFirstPoisons_RaiseOnPoisonedExactlyOnce_WithTheErrorThatWon() {
        var db = new TestDb();
        var raised = new List<DbError>();
        db.OnPoisoned = error => { lock (raised) raised.Add(error); };

        Parallel.For(0, 64, i => db.PoisonDatabase(DbError.ApplyFailed(new InvalidOperationException($"poison {i}"))));

        Assert.That(raised, Has.Count.EqualTo(1));
        Assert.That(db.Poison!.Value.ToException().Message, Is.EqualTo(raised[0].ToException().Message), "the handler saw the error that actually won.");
    }

    [TestCase(ErrorKind.WalDurabilityFailed, 74)]
    [TestCase(ErrorKind.WalDirectorySyncFailed, 74)]
    [TestCase(ErrorKind.MultiTxOutcomeUnknown, 74)]
    [TestCase(ErrorKind.ApplyFailed, 70)]
    public void ExitCodes_SeparateADiskThatFailedFromAnEngineBug(ErrorKind kind, int expected) {
        var error = kind switch {
            ErrorKind.WalDurabilityFailed => IoFailure(),
            ErrorKind.WalDirectorySyncFailed => DbError.WalDirectorySyncFailed(),
            ErrorKind.MultiTxOutcomeUnknown => DbError.MultiTxOutcomeUnknown(new IOException("injected")),
            _ => DbError.ApplyFailed(new InvalidOperationException("injected")),
        };

        Assert.That(UnrecoverableExitCodes.For(error), Is.EqualTo(expected));
    }

    // ---- the exit sequence ----

    [Test]
    public async Task ExitProcess_PrintsOneLine_ShutsDown_ThenExitsWithTheCode() {
        var steps = new List<string>();
        var stderr = new StringWriter();
        var handler = new UnrecoverableErrorHandler(UnrecoverableErrorPolicy.ExitProcess,
            exit: code => { lock (steps) steps.Add($"exit {code}"); },
            failFast: _ => { lock (steps) steps.Add("failFast"); },
            stderr: stderr, exitWatchdog: TimeSpan.FromMilliseconds(300)) {
            Shutdown = () => { lock (steps) steps.Add("shutdown"); return Task.CompletedTask; },
        };

        handler.Trigger("RootDb (Root)", IoFailure());
        await handler.Handled.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(500);

        Assert.That(steps, Is.EqualTo(new[] { "shutdown", "exit 74" }), "shutdown first, then the exit - and no watchdog once the exit returned.");
        Assert.That(stderr.ToString(), Does.Contain("RootDb (Root)").And.Contain("WalDurabilityFailed").And.Contain("Exiting with 74"));
    }

    [Test]
    public async Task TriggeredFromManyThreadsAtOnce_ExitsOnce() {
        var exits = 0;
        var handler = new UnrecoverableErrorHandler(UnrecoverableErrorPolicy.ExitProcess,
            exit: _ => Interlocked.Increment(ref exits), failFast: _ => { }, stderr: TextWriter.Null);

        Parallel.For(0, 32, i => handler.Trigger($"db{i}", IoFailure()));
        await handler.Handled.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(Volatile.Read(ref exits), Is.EqualTo(1));
    }

    [Test]
    public async Task AShutdownThatHangs_DoesNotStopTheExit() {
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new UnrecoverableErrorHandler(UnrecoverableErrorPolicy.ExitProcess,
            exit: code => exited.TrySetResult(code), failFast: _ => { }, stderr: TextWriter.Null,
            shutdownBudget: TimeSpan.FromMilliseconds(200)) {
            Shutdown = () => new TaskCompletionSource().Task,
        };

        var clock = Stopwatch.StartNew();
        handler.Trigger("RootDb (Root)", IoFailure());
        var code = await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(code, Is.EqualTo(74));
        Assert.That(clock.Elapsed, Is.LessThan(TimeSpan.FromSeconds(3)), "the shutdown budget bounds it.");
    }

    [Test]
    public async Task AnExitThatHangs_IsCutShortByTheWatchdog() {
        using var releaseExit = new ManualResetEventSlim(false);
        var failedFast = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new UnrecoverableErrorHandler(UnrecoverableErrorPolicy.ExitProcess,
            exit: _ => releaseExit.Wait(TimeSpan.FromSeconds(10)),
            failFast: message => failedFast.TrySetResult(message),
            stderr: TextWriter.Null, exitWatchdog: TimeSpan.FromMilliseconds(200));

        handler.Trigger("RootDb (Root)", IoFailure());
        var message = await failedFast.Task.WaitAsync(TimeSpan.FromSeconds(5));
        releaseExit.Set();

        Assert.That(message, Does.Contain("Exiting with 74"), "a hung ProcessExit handler must not leave a zombie the supervisor never restarts.");
    }

    [Test]
    public async Task AnExitThatThrows_StillEndsInTheWatchdogsFailFast() {
        // Regression: the watchdog's event used to be disposed by a `using` when exit threw, leaving the watchdog to die
        // with ObjectDisposedException instead of forcing the process down with the message.
        var failedFast = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new UnrecoverableErrorHandler(UnrecoverableErrorPolicy.ExitProcess,
            exit: _ => throw new InvalidOperationException("injected exit failure"),
            failFast: message => failedFast.TrySetResult(message),
            stderr: TextWriter.Null, exitWatchdog: TimeSpan.FromMilliseconds(200));

        handler.Trigger("RootDb (Root)", IoFailure());
        var message = await failedFast.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await handler.Handled.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(message, Does.Contain("Exiting with 74"));
    }

    [Test]
    public async Task CallbackPolicy_ReportsTheError_AndNeverExits() {
        var exits = 0;
        var reported = new TaskCompletionSource<UnrecoverableError>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new UnrecoverableErrorHandler(UnrecoverableErrorPolicy.Callback(info => reported.TrySetResult(info)),
            exit: _ => Interlocked.Increment(ref exits), failFast: _ => Interlocked.Increment(ref exits), stderr: TextWriter.Null);

        handler.Trigger("RootDb (Root)", DbError.ApplyFailed(new InvalidOperationException("injected")));
        var info = await reported.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await handler.Handled;

        Assert.That(info.ExitCode, Is.EqualTo(70));
        Assert.That(info.Database, Is.EqualTo("RootDb (Root)"));
        Assert.That(Volatile.Read(ref exits), Is.EqualTo(0));
    }

    // ---- host wiring ----

    private async Task<(RhinoHost Host, Task<UnrecoverableError> Reported)> BuildHostAsync(
        Func<RhinoHostBuilder, RhinoHostBuilder>? configure = null, Action? whileReporting = null) {
        RhinoHostConfigTestHelper.WriteConfig(dir, new HostConfig { ColdPath = dir, HttpEnabled = false, HttpPort = 0 });
        var reported = new TaskCompletionSource<UnrecoverableError>(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = RhinoHostBuilder.Create(dir)
            .OnUnrecoverableError(UnrecoverableErrorPolicy.Callback(info => {
                whileReporting?.Invoke();
                reported.TrySetResult(info);
            }))
            .AddDatabase<RootDb, DefaultTransaction>(o => o.CreateDb = cold => new RootDb(cold));
        builder = configure?.Invoke(builder) ?? builder;
        var host = (await builder.BuildAsync()).Unwrap();
        return (host, reported.Task);
    }

    static private void Shutdown(RhinoHost host) {
        var cold = host.GetDatabase<RootDb>().Cold!;
        host.Dispose();
        cold.Dispose();
    }

    [Test]
    public async Task APoisonedRoot_IsReported_ByName_WithTheDiskExitCode() {
        var (host, reported) = await BuildHostAsync();

        host.GetDatabase<RootDb>().PoisonDatabase(IoFailure());
        var info = await reported.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(info.Database, Is.EqualTo("RootDb (Root)"));
        Assert.That(info.ExitCode, Is.EqualTo(74));
        Shutdown(host);
    }

    [Test]
    public async Task APoisonedKeyedChild_IsReported_WithItsTypeAndKey() {
        var (host, reported) = await BuildHostAsync(b =>
            b.AddChildDatabase<ChildDb, DefaultTransaction, string>(o => o.CreateDb = cold => new ChildDb(cold)));
        var child = (await host.GetOrActivateChildAsync<ChildDb, DefaultTransaction, string>("s1")).Unwrap();

        child.PoisonDatabase(IoFailure());
        var info = await reported.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(info.Database, Is.EqualTo("ChildDb[s1]"), "a poisoned Child stops the whole engine too - there's no way to reopen just one from disk.");
        Shutdown(host);
    }

    [Test]
    public async Task APoisonedSingletonChild_IsReported() {
        var (host, reported) = await BuildHostAsync(b =>
            b.AddSingletonChildDatabase<ChildDb, DefaultTransaction>(o => o.CreateDb = cold => new ChildDb(cold)));
        var singleton = (await host.GetOrActivateChildAsync<ChildDb, DefaultTransaction, string>(SingletonChild.Key)).Unwrap();

        singleton.PoisonDatabase(IoFailure());
        var info = await reported.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(info.Database, Is.EqualTo($"ChildDb[{SingletonChild.Key}]"));
        Shutdown(host);
    }

    [Test]
    public async Task TheHandlerRunsOffThePoisoningThread_SoABlockingOneCantStallADatabaseLoop() {
        var (host, reported) = await BuildHostAsync(whileReporting: () => Thread.Sleep(1000));

        var clock = Stopwatch.StartNew();
        host.GetDatabase<RootDb>().PoisonDatabase(IoFailure());
        var returnedAfter = clock.Elapsed;
        await reported.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(returnedAfter, Is.LessThan(TimeSpan.FromMilliseconds(300)));
        Shutdown(host);
    }

    [Test]
    public async Task ARealWalFsyncFailure_FailsTheWrite_AndReportsTheDiskExitCode() {
        var (host, reported) = await BuildHostAsync();
        var root = host.GetDatabase<RootDb>();
        root.Cold!.Wal.TestOnlyBeforeFlush = () => throw new IOException("injected fsync failure");
        try {
            var write = await root.RunConfirmed(ctx => {
                root.Cold!.Stage(1, ChangeKind.Insert, [1], [1]);
                return Result.Ok();
            });
            var info = await reported.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.That(write.GetError().Kind, Is.EqualTo(ErrorKind.WalDurabilityFailed), "the write that hit the failure still gets its error.");
            Assert.That(info.ExitCode, Is.EqualTo(74));
            Assert.That(root.IsPoisoned, Is.True);
        } finally {
            root.Cold!.Wal.TestOnlyBeforeFlush = null;
        }
        Shutdown(host);
    }
}
