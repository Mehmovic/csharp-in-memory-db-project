using System.Collections.Concurrent;
using System.Reflection;
using System.Threading.Channels;
using RhinoDB.Core;

namespace RhinoDB.Lib.Execution.Test;

public class PooledOperationTests {
    private sealed class TestDbContext : DbContext<DefaultTransaction> { }

    static private Channel<IExecutionWorkItem> NewChannel() =>
        Channel.CreateUnbounded<IExecutionWorkItem>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    static private async Task<TValue> EnqueueAndRun<TValue, TArgs>(
        Channel<IExecutionWorkItem> channel,
        DbContext<DefaultTransaction> ctx,
        Func<DbContext<DefaultTransaction>, DefaultTransaction, TArgs, TValue> operation,
        TArgs args,
        PropagationMode mode = PropagationMode.Optimistic)
        where TValue : struct, IResult<TValue> {
        var valueTask = PooledOperation<DefaultTransaction, TValue, TArgs>.Enqueue(channel, ctx, operation, args, mode);
        var item = await channel.Reader.ReadAsync();
        item.Run();
        return await valueTask;
    }

    static private ConcurrentQueue<PooledOperation<DefaultTransaction, TValue, TArgs>> GetPool<TValue, TArgs>()
        where TValue : struct, IResult<TValue> {
        var field = typeof(PooledOperation<DefaultTransaction, TValue, TArgs>).GetField("Pool", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (ConcurrentQueue<PooledOperation<DefaultTransaction, TValue, TArgs>>)field.GetValue(null)!;
    }

    static private object? GetPrivateField<TValue, TArgs>(PooledOperation<DefaultTransaction, TValue, TArgs> instance, string name)
        where TValue : struct, IResult<TValue> {
        var field = typeof(PooledOperation<DefaultTransaction, TValue, TArgs>).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!;
        return field.GetValue(instance);
    }

    [Test]
    public async Task EnqueueAndRun_ReturningOk_CompletesWithTheValue() {
        var ctx = new TestDbContext();
        var channel = NewChannel();

        var result = await EnqueueAndRun<Result<int>, int>(channel, ctx, static (c, tx, args) => Result<int>.Ok(args), 42);

        Assert.That(result.IsOk(), Is.True);
        Assert.That(result.Unwrap(), Is.EqualTo(42));
    }

    [Test]
    public async Task EnqueueAndRun_NonGenericResult_OkAndError_RoundTrip() {
        var ctx = new TestDbContext();
        var channel = NewChannel();

        var ok = await EnqueueAndRun<Result, int>(channel, ctx, static (c, tx, _) => Result.Ok(), 0);
        var error = await EnqueueAndRun<Result, int>(channel, ctx, static (c, tx, _) => Result.Error(DbError.DuplicateKey()), 0);

        Assert.That(ok.IsOk(), Is.True);
        Assert.That(error.IsError(), Is.True);
        Assert.That(error.GetError().Kind, Is.EqualTo(ErrorKind.DuplicateKey));
    }

    [Test]
    public async Task EnqueueAndRun_OperationThrows_ProducesTheSameSystemFailureShapeAsToday() {
        var ctx = new TestDbContext();
        var channel = NewChannel();

        var result = await EnqueueAndRun<Result<int>, int>(channel, ctx, static (c, tx, _) => throw new InvalidOperationException("boom"), 0);

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetError().Kind, Is.EqualTo(ErrorKind.SystemFailure));
        Assert.That(result.GetError().ToException().Message, Is.EqualTo("boom"));
    }

    [Test]
    public async Task AfterConsumption_ASecondConsumptionOfTheSameValueTaskThrows() {
        var ctx = new TestDbContext();
        var channel = NewChannel();

        var valueTask = PooledOperation<DefaultTransaction, Result<int>, int>.Enqueue(
            channel, ctx, static (c, tx, args) => Result<int>.Ok(args), 42, PropagationMode.Optimistic);
        var item = await channel.Reader.ReadAsync();
        item.Run();

        var first = await valueTask;
        Assert.That(first.Unwrap(), Is.EqualTo(42));

        Assert.That(() => valueTask.GetAwaiter().GetResult(), Throws.InstanceOf<InvalidOperationException>());
    }

    [Test]
    public async Task AfterConsumption_TheSameUnderlyingInstanceIsReusedOnTheNextRent() {
        var ctx = new TestDbContext();
        var channel = NewChannel();
        var pool = GetPool<Result<int>, int>();

        await EnqueueAndRun<Result<int>, int>(channel, ctx, static (c, tx, args) => Result<int>.Ok(args), 1);
        pool.TryPeek(out var first);

        await EnqueueAndRun<Result<int>, int>(channel, ctx, static (c, tx, args) => Result<int>.Ok(args), 2);
        pool.TryPeek(out var second);

        Assert.That(first, Is.Not.Null);
        Assert.That(second, Is.SameAs(first), "The second round trip should reuse the same pooled instance, not allocate a new one.");
    }

    [Test]
    public async Task AfterConsumption_ContextAndOperationReferencesAreNulledOutBeforeReturningToThePool() {
        var ctx = new TestDbContext();
        var channel = NewChannel();
        var pool = GetPool<Result<int>, int>();

        await EnqueueAndRun<Result<int>, int>(channel, ctx, static (c, tx, args) => Result<int>.Ok(args), 1);

        Assert.That(pool.TryPeek(out var pooled), Is.True);
        Assert.That(GetPrivateField(pooled!, "context"), Is.Null, "A pooled instance sitting idle must not keep the previous operation's DbContext rooted.");
        Assert.That(GetPrivateField(pooled!, "operation"), Is.Null, "A pooled instance sitting idle must not keep the previous operation's delegate rooted.");
    }

    private readonly record struct ConcurrentTestArgs(int Value);

    [Test]
    public async Task ConcurrentEnqueueAndConsume_UnderMultipleThreads_EachCallerGetsItsOwnCorrectValue() {
        // A distinct TArgs (ConcurrentTestArgs, not plain int) gives this test its own isolated
        // static pool - many operations can be genuinely in flight at once here (unlike every
        // other, single-in-flight-at-a-time test above), so this must not share a pool with tests
        // that assert exact pool contents/reuse.
        var ctx = new TestDbContext();
        var channel = NewChannel();
        var readerTask = Task.Run(async () => {
            await foreach (var item in channel.Reader.ReadAllAsync()) item.Run();
        });

        const int producers = 8;
        const int perProducer = 100;
        var tasks = new Task[producers * perProducer];
        var index = 0;
        for (var p = 0; p < producers; p++) {
            for (var i = 0; i < perProducer; i++) {
                var expected = p * perProducer + i;
                tasks[index++] = Task.Run(async () => {
                    var valueTask = PooledOperation<DefaultTransaction, Result<int>, ConcurrentTestArgs>.Enqueue(
                        channel, ctx, static (c, tx, args) => Result<int>.Ok(args.Value), new ConcurrentTestArgs(expected), PropagationMode.Optimistic);
                    var result = await valueTask;
                    Assert.That(result.Unwrap(), Is.EqualTo(expected));
                });
            }
        }

        await Task.WhenAll(tasks);
        channel.Writer.Complete();
        await readerTask;
    }
}
