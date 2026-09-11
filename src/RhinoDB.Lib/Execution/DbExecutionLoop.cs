using System.Threading.Channels;
using RhinoDB.Lib.Cold;

namespace RhinoDB.Lib.Execution;

internal sealed class DbExecutionLoop {
    private readonly Channel<Action> channel = Channel.CreateUnbounded<Action>(
        new UnboundedChannelOptions {
            SingleReader = true,
            SingleWriter = false,
        }
    );

    private readonly DbContext context;

    public DbExecutionLoop(DbContext context) {
        this.context = context;
        _ = Task.Factory.StartNew(RunLoop, TaskCreationOptions.LongRunning);
    }

    public Task<Result<T>> Enqueue<T>(Func<DbContext, Result<T>> operation, PropagationMode mode) {
        var tcs = new TaskCompletionSource<Result<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pushed = channel.Writer.TryWrite(() => {
                context.Cold?.BeginScope();
                Result<T> result;
                try { result = operation.Invoke(context); }
                catch (Exception ex) { result = Result<T>.Error(ex); }

                Complete(tcs, result, context.Cold?.EndScope(commit: result.IsOk(), forceSync: mode == PropagationMode.Confirmed));
            }
        );

        if (!pushed) tcs.SetResult(Result<T>.Error(DbError.ProcedureCreationFailed()));
        return tcs.Task;
    }

    public Task<Result> Enqueue(Func<DbContext, Result> operation, PropagationMode mode) {
        var tcs = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pushed = channel.Writer.TryWrite(() => {
                context.Cold?.BeginScope();
                Result result;
                try { result = operation.Invoke(context); }
                catch (Exception ex) { result = Result.Error(ex); }

                Complete(tcs, result, context.Cold?.EndScope(commit: result.IsOk(), forceSync: mode == PropagationMode.Confirmed));
            }
        );

        if (!pushed) tcs.SetResult(Result.Error(DbError.ProcedureCreationFailed()));
        return tcs.Task;
    }

    public Task<Result<T>> Enqueue<TArgs, T>(Func<DbContext, TArgs, Result<T>> operation, TArgs args, PropagationMode mode) {
        var tcs = new TaskCompletionSource<Result<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pushed = channel.Writer.TryWrite(() => {
                context.Cold?.BeginScope();
                Result<T> result;
                try { result = operation.Invoke(context, args); }
                catch (Exception ex) { result = Result<T>.Error(ex); }

                Complete(tcs, result, context.Cold?.EndScope(commit: result.IsOk(), forceSync: mode == PropagationMode.Confirmed));
            }
        );

        if (!pushed) tcs.SetResult(Result<T>.Error(DbError.ProcedureCreationFailed()));
        return tcs.Task;
    }

    public Task<Result> Enqueue<TArgs>(Func<DbContext, TArgs, Result> operation, TArgs args, PropagationMode mode) {
        var tcs = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pushed = channel.Writer.TryWrite(() => {
                context.Cold?.BeginScope();
                Result result;
                try { result = operation.Invoke(context, args); }
                catch (Exception ex) { result = Result.Error(ex); }

                Complete(tcs, result, context.Cold?.EndScope(commit: result.IsOk(), forceSync: mode == PropagationMode.Confirmed));
            }
        );

        if (!pushed) tcs.SetResult(Result.Error(DbError.ProcedureCreationFailed()));
        return tcs.Task;
    }

    public Task<Result> Enqueue<TTx>(Func<DbContext, TTx, Result> operation, PropagationMode mode) where TTx : ITransaction {
        var tcs = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pushed = channel.Writer.TryWrite(() => {
                context.Cold?.BeginScope();
                Result result;
                try {
                    ITransaction tx = context.CreateTransaction()!;
                    result = operation.Invoke(context, (TTx)tx);
                    if (result.IsOk()) {
                        Result applyResult = tx.Apply();
                        if (applyResult.IsError()) result = applyResult;
                    }
                }
                catch (Exception ex) { result = Result.Error(ex); }

                Complete(tcs, result, context.Cold?.EndScope(commit: result.IsOk(), forceSync: mode == PropagationMode.Confirmed));
            }
        );

        if (!pushed) tcs.SetResult(Result.Error(DbError.ProcedureCreationFailed()));
        return tcs.Task;
    }

    public Task<Result<T>> Enqueue<T, TTx>(Func<DbContext, TTx, Result<T>> operation, PropagationMode mode) where TTx : ITransaction {
        var tcs = new TaskCompletionSource<Result<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pushed = channel.Writer.TryWrite(() => {
                context.Cold?.BeginScope();
                Result<T> result;
                try {
                    ITransaction tx = context.CreateTransaction()!;
                    result = operation.Invoke(context, (TTx)tx);
                    if (result.IsOk()) {
                        Result applyResult = tx.Apply();
                        if (applyResult.IsError()) result = applyResult;
                    }
                }
                catch (Exception ex) { result = Result<T>.Error(ex); }

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
