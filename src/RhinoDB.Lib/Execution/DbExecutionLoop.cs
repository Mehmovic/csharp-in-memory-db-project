using System.Threading.Channels;
using RhinoDB.Lib.Cold;

namespace RhinoDB.Lib.Execution;

internal sealed class DbExecutionLoop<TTx> where TTx : ITransaction {
    private readonly Channel<Action> channel = Channel.CreateUnbounded<Action>(
        new UnboundedChannelOptions {
            SingleReader = true,
            SingleWriter = false,
        }
    );

    private readonly DbContext<TTx> context;

    public DbExecutionLoop(DbContext<TTx> context) {
        this.context = context;
        _ = Task.Factory.StartNew(RunLoop, TaskCreationOptions.LongRunning);
    }

    public Task<Result> Enqueue(Func<DbContext<TTx>, TTx, Result> operation, PropagationMode mode) {
        var tcs = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pushed = channel.Writer.TryWrite(() => {
                context.Cold?.BeginScope();
                Result result;
                TTx tx = default!;
                var txCreated = false;
                try {
                    tx = context.CreateTransaction();
                    txCreated = true;
                    result = operation.Invoke(context, tx);
                    if (result.IsOk()) {
                        Result applyResult = tx.Apply();
                        if (applyResult.IsError()) result = applyResult;
                    }
                }
                catch (Exception ex) { result = Result.Error(ex); }
                if (txCreated && result.IsError()) tx.Discard();

                Complete(tcs, result, context.Cold?.EndScope(commit: result.IsOk(), forceSync: mode == PropagationMode.Confirmed));
            }
        );

        if (!pushed) tcs.SetResult(Result.Error(DbError.ProcedureCreationFailed()));
        return tcs.Task;
    }

    public Task<Result<T>> Enqueue<T>(Func<DbContext<TTx>, TTx, Result<T>> operation, PropagationMode mode) {
        var tcs = new TaskCompletionSource<Result<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pushed = channel.Writer.TryWrite(() => {
                context.Cold?.BeginScope();
                Result<T> result;
                TTx tx = default!;
                var txCreated = false;
                try {
                    tx = context.CreateTransaction();
                    txCreated = true;
                    result = operation.Invoke(context, tx);
                    if (result.IsOk()) {
                        Result applyResult = tx.Apply();
                        if (applyResult.IsError()) result = applyResult;
                    }
                }
                catch (Exception ex) { result = Result<T>.Error(ex); }
                if (txCreated && result.IsError()) tx.Discard();

                Complete(tcs, result, context.Cold?.EndScope(commit: result.IsOk(), forceSync: mode == PropagationMode.Confirmed));
            }
        );

        if (!pushed) tcs.SetResult(Result<T>.Error(DbError.ProcedureCreationFailed()));
        return tcs.Task;
    }

    public Task<Result> Enqueue<TArgs>(Func<DbContext<TTx>, TTx, TArgs, Result> operation, TArgs args, PropagationMode mode) {
        var tcs = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pushed = channel.Writer.TryWrite(() => {
                context.Cold?.BeginScope();
                Result result;
                TTx tx = default!;
                var txCreated = false;
                try {
                    tx = context.CreateTransaction();
                    txCreated = true;
                    result = operation.Invoke(context, tx, args);
                    if (result.IsOk()) {
                        Result applyResult = tx.Apply();
                        if (applyResult.IsError()) result = applyResult;
                    }
                }
                catch (Exception ex) { result = Result.Error(ex); }
                if (txCreated && result.IsError()) tx.Discard();

                Complete(tcs, result, context.Cold?.EndScope(commit: result.IsOk(), forceSync: mode == PropagationMode.Confirmed));
            }
        );

        if (!pushed) tcs.SetResult(Result.Error(DbError.ProcedureCreationFailed()));
        return tcs.Task;
    }

    public Task<Result<T>> Enqueue<T, TArgs>(Func<DbContext<TTx>, TTx, TArgs, Result<T>> operation, TArgs args, PropagationMode mode) {
        var tcs = new TaskCompletionSource<Result<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pushed = channel.Writer.TryWrite(() => {
                context.Cold?.BeginScope();
                Result<T> result;
                TTx tx = default!;
                var txCreated = false;
                try {
                    tx = context.CreateTransaction();
                    txCreated = true;
                    result = operation.Invoke(context, tx, args);
                    if (result.IsOk()) {
                        Result applyResult = tx.Apply();
                        if (applyResult.IsError()) result = applyResult;
                    }
                }
                catch (Exception ex) { result = Result<T>.Error(ex); }
                if (txCreated && result.IsError()) tx.Discard();

                Complete(tcs, result, context.Cold?.EndScope(commit: result.IsOk(), forceSync: mode == PropagationMode.Confirmed));
            }
        );

        if (!pushed) tcs.SetResult(Result<T>.Error(DbError.ProcedureCreationFailed()));
        return tcs.Task;
    }

    private async Task RunLoop() {
        await foreach (Action action in channel.Reader.ReadAllAsync()) {
            action.Invoke();
        }
    }

    static private void Complete<T>(TaskCompletionSource<Result<T>> tcs, Result<T> result, Task<int>? syncTask) {
        if (syncTask is null || syncTask.IsCompleted) {
            tcs.SetResult(Finalize(result, syncTask));
            return;
        }
        syncTask.ContinueWith(t => tcs.SetResult(Finalize(result, t)), TaskScheduler.Default);
    }

    static private void Complete(TaskCompletionSource<Result> tcs, Result result, Task<int>? syncTask) {
        if (syncTask is null || syncTask.IsCompleted) {
            tcs.SetResult(Finalize(result, syncTask));
            return;
        }
        syncTask.ContinueWith(t => tcs.SetResult(Finalize(result, t)), TaskScheduler.Default);
    }

    static private Result<T> Finalize<T>(Result<T> result, Task<int>? syncTask) =>
        syncTask is { Result: var rc } && rc != 0 ? Result<T>.Error(MdbxErrorMapper.Map(rc)) : result;

    static private Result Finalize(Result result, Task<int>? syncTask) =>
        syncTask is { Result: var rc } && rc != 0 ? Result.Error(MdbxErrorMapper.Map(rc)) : result;
}
