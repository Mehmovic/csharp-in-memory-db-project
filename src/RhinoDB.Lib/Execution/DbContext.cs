using RhinoDB.Lib.Cold;

namespace RhinoDB.Lib.Execution;

public class DbContext {
    private readonly DbExecutionLoop executionLoop;

    internal ColdStore? Cold { get; }

    public DbContext() {
        executionLoop = new DbExecutionLoop(this);
    }

    public DbContext(ColdStore cold) {
        Cold = cold;
        executionLoop = new DbExecutionLoop(this);
    }

    public Task<Result> Run(Func<DbContext, Result> func, PropagationMode mode = PropagationMode.Optimistic) {
        return executionLoop.Enqueue(func, mode);
    }

    public Task<Result<T>> Run<T>(Func<DbContext, Result<T>> func, PropagationMode mode = PropagationMode.Optimistic) {
        return executionLoop.Enqueue(func, mode);
    }

    public Task<Result> Run<TArgs>(Func<DbContext, TArgs, Result> func, TArgs args, PropagationMode mode = PropagationMode.Optimistic) {
        return executionLoop.Enqueue(func, args, mode);
    }

    public Task<Result<T>> Run<T, TArgs>(Func<DbContext, TArgs, Result<T>> func, TArgs args, PropagationMode mode = PropagationMode.Optimistic) {
        return executionLoop.Enqueue(func, args, mode);
    }

    public Task<Result> RunConfirmed(Func<DbContext, Result> func)
        => Run(func, PropagationMode.Confirmed);

    public Task<Result<T>> RunConfirmed<T>(Func<DbContext, Result<T>> func)
        => Run(func, PropagationMode.Confirmed);

    public Task<Result> RunConfirmed<TArgs>(Func<DbContext, TArgs, Result> func, TArgs args)
        => Run(func, args, PropagationMode.Confirmed);

    public Task<Result<T>> RunConfirmed<T, TArgs>(Func<DbContext, TArgs, Result<T>> func, TArgs args)
        => Run(func, args, PropagationMode.Confirmed);
}
