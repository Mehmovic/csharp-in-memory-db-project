using System.Collections.Concurrent;
using System.Threading.Channels;
using System.Threading.Tasks.Sources;

using RhinoDB.Core.Exceptions;

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
    private TValue pendingResult;

    private PooledOperation() => core.RunContinuationsAsynchronously = true;

    public TValue GetResult(short token) {
        var result = core.GetResult(token);

        context = null;
        operation = null;
        args = default!;
        pendingResult = default!;
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
        var cold = ctx.Cold;
        if (ctx.Poison is { } poisoned) {
            Complete(TValue.FromError(poisoned), ctx, null);
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
                try {
                    var applyResult = tx.Apply();
                    if (applyResult.IsError()) result = TValue.FromError(applyResult.GetError());
                }
                catch (ApplyFailedException ex) {
                    var error = DbError.ApplyFailed(ex.InnerException);
                    ctx.PoisonDatabase(error);
                    result = TValue.FromError(error);
                }
            }
        }
        catch (Exception ex) { result = TValue.FromException(ex); }
        if (txCreated && !result.IsOk()) tx.Discard();

        try {
            Complete(result, ctx, cold?.EndScope(commit: result.IsOk(), mode, tx.LastLsn ?? 0));
        }
        catch (Exception ex) {
            ctx.PoisonDatabase(DbError.ApplyFailed(ex));
            try { core.SetResult(TValue.FromException(ex)); } catch { /* already completed */ }
        }

        if (txCreated && result.IsOkOrReverted() && ctx.Cleanup.ShouldSweep(tx.PendingStorageOrphanCount)) {
            try { tx.SweepDeleted(); } catch { /* garbage stays until the next sweep */ }
        }
    }

    private void Complete(TValue result, DbContext<TTx> ctx, Task<DbError?>? durabilityTask) {
        if (durabilityTask is null || durabilityTask.IsCompleted) {
            core.SetResult(Finalize(result, durabilityTask, ctx));
            return;
        }
        pendingResult = result;
        durabilityTask.ContinueWith(static (t, state) => {
            var self = (PooledOperation<TTx, TValue, TArgs>)state!;
            self.core.SetResult(Finalize(self.pendingResult, t, self.context!));
        }, this, TaskScheduler.Default);
    }

    static private TValue Finalize(TValue result, Task<DbError?>? durabilityTask, DbContext<TTx> ctx) {
        if (durabilityTask is not { Result: { } err }) return result;
        
        ctx.PoisonDatabase(err);
        return TValue.FromError(err);
    }
}
