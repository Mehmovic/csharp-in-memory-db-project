using RhinoDB.Core;

namespace RhinoDB.Lib.Execution.Test;

public class DbContextTests {
    private readonly record struct AddArgs(int Left, int Right);

    private sealed class CounterDbContext : DbContext {
        public int Counter;
    }

    // ---- Basic round-trip ----

    [Test]
    public async Task Run_ReturningOk_CompletesWithTheValue() {
        var ctx = new DbContext();

        var result = await ctx.Run(c => Result.Ok(42));

        Assert.That(result.IsOk(), Is.True);
        Assert.That(result.Unwrap(), Is.EqualTo(42));
    }

    [Test]
    public async Task Run_ReturningError_CompletesWithTheSameDbErrorKind() {
        var ctx = new DbContext();

        var result = await ctx.Run<int>(c => Result<int>.Error(DbError.IndexKeyNotFound()));

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetError().Kind, Is.EqualTo(ErrorKind.IndexKeyNotFound));
    }

    [Test]
    public async Task Run_NonGeneric_OkAndError_RoundTrip() {
        var ctx = new DbContext();

        Result ok = await ctx.Run(c => Result.Ok());
        Result error = await ctx.Run(c => Result.Error(DbError.DuplicateKey()));

        Assert.That(ok.IsOk(), Is.True);
        Assert.That(error.IsError(), Is.True);
        Assert.That(error.GetError().Kind, Is.EqualTo(ErrorKind.DuplicateKey));
    }

    [Test]
    public async Task Run_PropagationModeParameter_BothValuesBehaveIdenticallyForNow() {
        var ctx = new DbContext();

        var confirmed = await ctx.Run(c => Result.Ok(1), PropagationMode.Confirmed);
        var optimistic = await ctx.Run(c => Result.Ok(1), PropagationMode.Optimistic);

        Assert.That(confirmed.Unwrap(), Is.EqualTo(optimistic.Unwrap()));
    }

    // ---- TArgs overloads ----

    [Test]
    public async Task Run_WithTArgs_GenericOverload_PassesTheArgsThroughWithoutCapture() {
        var ctx = new DbContext();

        var result = await ctx.Run(static (c, args) => Result.Ok(args.Left + args.Right), new AddArgs(2, 3));

        Assert.That(result.IsOk(), Is.True);
        Assert.That(result.Unwrap(), Is.EqualTo(5));
    }

    [Test]
    public async Task Run_WithTArgs_NonGenericOverload_PassesTheArgsThrough() {
        var ctx = new DbContext();

        Result result = await ctx.Run(
            static (c, shouldFail) => shouldFail ? Result.Error(DbError.DuplicateKey()) : Result.Ok(),
            false);

        Assert.That(result.IsOk(), Is.True);
    }

    // ---- Subclassed context ----

    [Test]
    public async Task Run_OnASubclassedContext_CanAccessSubclassState() {
        var ctx = new CounterDbContext();

        var result = await ctx.Run(c => {
            var counterCtx = (CounterDbContext)c;
            counterCtx.Counter += 1;
            return Result.Ok(counterCtx.Counter);
        });

        Assert.That(result.Unwrap(), Is.EqualTo(1));
    }

    // ---- Ordering / mutual exclusion ----

    [Test]
    public async Task ConcurrentRunCalls_ProcessOneAtATime_NoTornOrInterleavedWrites() {
        var ctx = new DbContext();
        var log = new List<int>();
        const int n = 200;

        var tasks = new Task[n];
        for (var i = 0; i < n; i++) {
            var captured = i;
            tasks[i] = ctx.Run(c => { log.Add(captured); return Result.Ok(); });
        }

        await Task.WhenAll(tasks);

        Assert.That(log, Is.EqualTo(Enumerable.Range(0, n).ToList()));
    }

    [Test]
    public async Task ConcurrentRunCalls_FromMultipleThreads_StillProcessOneAtATimeWithNoLostOrTornWrites() {
        // The single-thread version above proves FIFO ordering; this proves the
        // underlying channel's SingleWriter=false claim - genuinely concurrent
        // producers (not just a tight sequential loop on one thread) still can't
        // corrupt or lose writes, because DbExecutionLoop only ever has one reader
        // draining them.
        var ctx = new DbContext();
        var log = new List<int>();
        const int producers = 8;
        const int perProducer = 50;

        var producerTasks = Enumerable.Range(0, producers).Select(_ => Task.Run(async () => {
            var tasks = new Task[perProducer];
            for (var i = 0; i < perProducer; i++) {
                tasks[i] = ctx.Run(c => { log.Add(1); return Result.Ok(); });
            }
            await Task.WhenAll(tasks);
        })).ToArray();

        await Task.WhenAll(producerTasks);

        Assert.That(log.Count, Is.EqualTo(producers * perProducer));
        Assert.That(log, Has.All.EqualTo(1));
    }

    // ---- Exception safety ----

    [Test]
    public async Task Run_WhenOperationThrows_SurfacesAsASystemFailureResultCarryingTheOriginalException() {
        var ctx = new DbContext();

        var result = await ctx.Run<int>(c => throw new InvalidOperationException("boom"));

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetError().Kind, Is.EqualTo(ErrorKind.SystemFailure));
        Assert.That(result.GetError().ToException(), Is.InstanceOf<InvalidOperationException>());
        Assert.That(result.GetError().ToException().Message, Is.EqualTo("boom"));
    }

    [Test]
    public async Task Run_AfterAPriorOperationThrew_StillProcessesSubsequentOperations() {
        var ctx = new DbContext();

        var faulted = await ctx.Run<int>(c => throw new InvalidOperationException("boom"));
        Assert.That(faulted.IsError(), Is.True);

        var result = await ctx.Run(c => Result.Ok(42));

        Assert.That(result.IsOk(), Is.True);
        Assert.That(result.Unwrap(), Is.EqualTo(42));
    }

    [Test]
    public async Task Run_ReturningErrorFromACaughtException_AlsoProducesASystemFailureResult() {
        // Result<T>.Error(Exception)/Result.Error(Exception) let an operation that
        // catches its own exception report it the same way an uncaught one comes
        // back from DbExecutionLoop - one SystemFailure shape either way.
        var ctx = new DbContext();

        var result = await ctx.Run<int>(c => {
            try { throw new InvalidOperationException("caught locally"); }
            catch (Exception ex) { return Result<int>.Error(ex); }
        });

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetError().Kind, Is.EqualTo(ErrorKind.SystemFailure));
        Assert.That(result.GetError().ToException().Message, Is.EqualTo("caught locally"));
    }

    // ---- RunConfirmed convenience overloads ----

    [Test]
    public async Task RunConfirmed_NonGeneric_BehavesLikeRunWithConfirmedMode() {
        var ctx = new DbContext();

        Result ok = await ctx.RunConfirmed(c => Result.Ok());
        Result error = await ctx.RunConfirmed(c => Result.Error(DbError.DuplicateKey()));

        Assert.That(ok.IsOk(), Is.True);
        Assert.That(error.IsError(), Is.True);
        Assert.That(error.GetError().Kind, Is.EqualTo(ErrorKind.DuplicateKey));
    }

    [Test]
    public async Task RunConfirmed_Generic_ReturnsTheValue() {
        var ctx = new DbContext();

        var result = await ctx.RunConfirmed(c => Result.Ok(42));

        Assert.That(result.IsOk(), Is.True);
        Assert.That(result.Unwrap(), Is.EqualTo(42));
    }

    [Test]
    public async Task RunConfirmed_WithTArgs_NonGeneric_PassesTheArgsThrough() {
        var ctx = new DbContext();

        Result result = await ctx.RunConfirmed(
            static (c, shouldFail) => shouldFail ? Result.Error(DbError.DuplicateKey()) : Result.Ok(),
            false);

        Assert.That(result.IsOk(), Is.True);
    }

    [Test]
    public async Task RunConfirmed_WithTArgs_Generic_PassesTheArgsThroughWithoutCapture() {
        var ctx = new DbContext();

        var result = await ctx.RunConfirmed(static (c, args) => Result.Ok(args.Left + args.Right), new AddArgs(2, 3));

        Assert.That(result.IsOk(), Is.True);
        Assert.That(result.Unwrap(), Is.EqualTo(5));
    }

    // ---- No adjacency guarantee across separate Run calls ----

    [Test]
    public async Task SeparateRunCalls_HaveNoAdjacencyGuarantee_AnUnrelatedCallCanInterleaveBetweenThem() {
        var ctx = new DbContext();
        var log = new List<string>();

        // Fully awaited: nothing else can be enqueued while this is in flight.
        await ctx.Run(c => { log.Add("first"); return Result.Ok(); });

        // Enqueue (synchronously, inside Run) an unrelated call before the "caller's"
        // second call gets enqueued - proving nothing pins these two calls adjacent
        // to each other just because they came from the same logical caller.
        var interloper = ctx.Run(c => { log.Add("interloper"); return Result.Ok(); });
        var second = ctx.Run(c => { log.Add("second"); return Result.Ok(); });

        await Task.WhenAll(interloper, second);

        Assert.That(log, Is.EqualTo(new[] { "first", "interloper", "second" }));
    }
}
