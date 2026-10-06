using System.Threading.Channels;

namespace RhinoDB.Lib.Execution;

internal sealed class DbExecutionLoop<TTx> where TTx : ITransaction {
    private readonly Channel<IExecutionWorkItem> channel = Channel.CreateUnbounded<IExecutionWorkItem>(
        new UnboundedChannelOptions {
            SingleReader = true,
            SingleWriter = false,
        }
    );

    private readonly DbContext<TTx> context;
    private Task? runLoopTask;
    private volatile bool draining;

    public DbExecutionLoop(DbContext<TTx> context, bool startPaused = false) {
        this.context = context;
        if (!startPaused) Resume();
    }

    public void Resume() {
        if (runLoopTask is not null) return;
        runLoopTask = Task.Factory.StartNew(RunLoop, TaskCreationOptions.LongRunning).Unwrap();
    }

    public void BeginDraining() {
        draining = true;
        channel.Writer.TryComplete();
    }

    public Task DrainAsync() => runLoopTask ?? Task.CompletedTask;

    public ValueTask<Result> Enqueue(Func<DbContext<TTx>, TTx, Result> operation, PropagationMode mode) {
        if (draining) return ValueTask.FromResult(Result.Error(DbError.DatabaseClosing()));
        return PooledOperation<TTx, Result, Func<DbContext<TTx>, TTx, Result>>.Enqueue(
            channel, context, static (ctx, tx, op) => op(ctx, tx), operation, mode);
    }

    public ValueTask<Result<T>> Enqueue<T>(Func<DbContext<TTx>, TTx, Result<T>> operation, PropagationMode mode) {
        if (draining) return ValueTask.FromResult(Result<T>.Error(DbError.DatabaseClosing()));
        return PooledOperation<TTx, Result<T>, Func<DbContext<TTx>, TTx, Result<T>>>.Enqueue(
            channel, context, static (ctx, tx, op) => op(ctx, tx), operation, mode);
    }

    public ValueTask<Result> Enqueue<TArgs>(Func<DbContext<TTx>, TTx, TArgs, Result> operation, TArgs args, PropagationMode mode) {
        if (draining) return ValueTask.FromResult(Result.Error(DbError.DatabaseClosing()));
        return PooledOperation<TTx, Result, TArgs>.Enqueue(channel, context, operation, args, mode);
    }

    public ValueTask<Result<T>> Enqueue<T, TArgs>(Func<DbContext<TTx>, TTx, TArgs, Result<T>> operation, TArgs args, PropagationMode mode) {
        if (draining) return ValueTask.FromResult(Result<T>.Error(DbError.DatabaseClosing()));
        return PooledOperation<TTx, Result<T>, TArgs>.Enqueue(channel, context, operation, args, mode);
    }

    internal bool TryEnqueue(IExecutionWorkItem item) => !draining && channel.Writer.TryWrite(item);

    private async Task RunLoop() {
        await foreach (var item in channel.Reader.ReadAllAsync()) {
            try {
                if (item is IAsyncExecutionWorkItem asyncItem) await asyncItem.RunAsync();
                else item.Run();
            }
            catch (Exception) { /* best afford, should not be called */ }
        }
    }
}
