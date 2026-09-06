using System.Threading.Channels;

namespace RhinoDB.Lib.Execution;

internal sealed class DbExecutionLoop {
    private readonly Channel<Func<PropagationMode>> channel = Channel.CreateUnbounded<Func<PropagationMode>>(
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
                try { tcs.SetResult(operation.Invoke(context)); } 
                catch (Exception ex) { tcs.SetResult(Result<T>.Error(ex)); }
                return mode;
            }
        );
        
        if (!pushed) tcs.SetResult(Result<T>.Error(DbError.ProcedureCreationFailed()));
        return tcs.Task;
    }

    public Task<Result> Enqueue(Func<DbContext, Result> operation, PropagationMode mode) {
        var tcs = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pushed = channel.Writer.TryWrite(() => {
                try { tcs.SetResult(operation.Invoke(context)); }
                catch (Exception ex) { tcs.SetResult(Result.Error(ex)); }
                return mode;
            }
        );
        
        if (!pushed) tcs.SetResult(Result.Error(DbError.ProcedureCreationFailed()));
        return tcs.Task;
    }

    public Task<Result<T>> Enqueue<TArgs, T>(Func<DbContext, TArgs, Result<T>> operation, TArgs args, PropagationMode mode) {
        var tcs = new TaskCompletionSource<Result<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pushed = channel.Writer.TryWrite(() => {
                try { tcs.SetResult(operation.Invoke(context, args)); }
                catch (Exception ex) { tcs.SetResult(Result<T>.Error(ex)); }
                return mode;
            }
        );
        
        if (!pushed) tcs.SetResult(Result<T>.Error(DbError.ProcedureCreationFailed()));
        return tcs.Task;
    }

    public Task<Result> Enqueue<TArgs>(Func<DbContext, TArgs, Result> operation, TArgs args, PropagationMode mode) {
        var tcs = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pushed = channel.Writer.TryWrite(() => {
                try { tcs.SetResult(operation.Invoke(context, args)); }
                catch (Exception ex) { tcs.SetResult(Result.Error(ex)); }
                return mode;
            }
        );
        
        if (!pushed) tcs.SetResult(Result.Error(DbError.ProcedureCreationFailed()));
        return tcs.Task;
    }

    private async Task RunLoop() {
        await foreach (var action in channel.Reader.ReadAllAsync()) {
            PropagationMode _ = action.Invoke();
        }
    }
}
