using System.Collections.Concurrent;
using System.Threading.Channels;
using System.Threading.Tasks.Sources;

using RhinoDB.Lib.Cold;

namespace RhinoDB.Lib.Execution;

internal interface IExecutionWorkItem {
    void Run();
}

internal sealed class PooledOperation<TTx, TValue, TArgs> : IValueTaskSource<TValue>, IExecutionWorkItem
    where TTx : ITransaction
    where TValue : struct, IResult<TValue> {
    static private readonly ConcurrentQueue<PooledOperation<TTx, TValue, TArgs>> Pool =
        new ConcurrentQueue<PooledOperation<TTx, TValue, TArgs>>();

    private ManualResetValueTaskSourceCore<TValue> core;
    private DbContext<TTx>? context;
    private Func<DbContext<TTx>, TTx, TArgs, TValue>? operation;
    private TArgs args = default!;
    private PropagationMode mode;

    private PooledOperation() => core.RunContinuationsAsynchronously = true;
    
    public TValue GetResult(short token) {
        TValue result = core.GetResult(token);

        context = null;
        operation = null;
        args = default!;
        core.Reset();
        Pool.Enqueue(this);

        return result;
    }

    public ValueTaskSourceStatus GetStatus(short token) => core.GetStatus(token);

    public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
        core.OnCompleted(continuation, state, token, flags);

    static internal ValueTask<TValue> Enqueue(
        Channel<IExecutionWorkItem> channel,
        DbContext<TTx> context,
        Func<DbContext<TTx>, TTx, TArgs, TValue> operation,
        TArgs args,
        PropagationMode mode) {
        var item = Pool.TryDequeue(out var pooled) ? pooled : new PooledOperation<TTx, TValue, TArgs>();
        item.context = context;
        item.operation = operation;
        item.args = args;
        item.mode = mode;

        var token = item.core.Version;
        if (!channel.Writer.TryWrite(item)) item.core.SetResult(TValue.FromError(DbError.ProcedureCreationFailed()));

        return new ValueTask<TValue>(item, token);
    }

    public void Run() {
        var ctx = context!;
        ColdStore? cold = ctx.Cold;
        if (cold is { IsDurabilityPoisoned: true }) {
            Complete(TValue.FromError(DbError.WalDurabilityFailed()), ctx, null);
            return;
        }

        cold?.BeginScope();
        TValue result;
        TTx tx = default!;
        var txCreated = false;
        try {
            tx = ctx.CreateTransaction();
            txCreated = true;
            result = operation!.Invoke(ctx, tx, args);
            if (result.IsOk()) {
                Result applyResult = tx.Apply();
                if (applyResult.IsError()) result = TValue.FromError(applyResult.GetError());
            }
        }
        catch (Exception ex) { result = TValue.FromException(ex); }
        if (txCreated && !result.IsOk()) tx.Discard();

        Complete(result, ctx, cold?.EndScope(commit: result.IsOk(), mode));
    }

    private void Complete(TValue result, DbContext<TTx> ctx, Task<DbError?>? durabilityTask) {
        if (durabilityTask is null || durabilityTask.IsCompleted) {
            core.SetResult(Finalize(result, durabilityTask, ctx));
            return;
        }
        durabilityTask.ContinueWith(t => core.SetResult(Finalize(result, t, ctx)), TaskScheduler.Default);
    }

    static private TValue Finalize(TValue result, Task<DbError?>? durabilityTask, DbContext<TTx> ctx) {
        if (durabilityTask is not { Result: { } err }) return result;
        
        ctx.Cold?.PoisonDurability(err);
        return TValue.FromError(err);
    }
}
