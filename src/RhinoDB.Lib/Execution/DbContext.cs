using RhinoDB.Lib.Cold;

namespace RhinoDB.Lib.Execution;

public class DbContext<TTx> where TTx : ITransaction {
    private readonly DbExecutionLoop<TTx> executionLoop;

    internal ColdStore? Cold { get; }

    protected DbContext(bool startPaused = false) {
        executionLoop = new DbExecutionLoop<TTx>(this, startPaused);
    }

    protected DbContext(ColdStore cold, bool startPaused = false) {
        Cold = cold;
        executionLoop = new DbExecutionLoop<TTx>(this, startPaused);
    }

    protected internal void ResumeExecution() => executionLoop.Resume();

    protected internal virtual TTx CreateTransaction() => default!;

    public ValueTask<Result> Run(Func<DbContext<TTx>, TTx, Result> func, PropagationMode mode = PropagationMode.Optimistic)
        => executionLoop.Enqueue(func, mode);

    public ValueTask<Result<T>> Run<T>(Func<DbContext<TTx>, TTx, Result<T>> func, PropagationMode mode = PropagationMode.Optimistic)
        => executionLoop.Enqueue(func, mode);

    public ValueTask<Result> Run<TArgs>(Func<DbContext<TTx>, TTx, TArgs, Result> func, TArgs args, PropagationMode mode = PropagationMode.Optimistic)
        => executionLoop.Enqueue(func, args, mode);

    public ValueTask<Result<T>> Run<T, TArgs>(Func<DbContext<TTx>, TTx, TArgs, Result<T>> func, TArgs args, PropagationMode mode = PropagationMode.Optimistic)
        => executionLoop.Enqueue(func, args, mode);

    public ValueTask<Result> RunConfirmed(Func<DbContext<TTx>, TTx, Result> func)
        => Run(func, PropagationMode.Confirmed);

    public ValueTask<Result<T>> RunConfirmed<T>(Func<DbContext<TTx>, TTx, Result<T>> func)
        => Run(func, PropagationMode.Confirmed);

    public ValueTask<Result> RunConfirmed<TArgs>(Func<DbContext<TTx>, TTx, TArgs, Result> func, TArgs args)
        => Run(func, args, PropagationMode.Confirmed);

    public ValueTask<Result<T>> RunConfirmed<T, TArgs>(Func<DbContext<TTx>, TTx, TArgs, Result<T>> func, TArgs args)
        => Run(func, args, PropagationMode.Confirmed);
}

public class DbContext : DbContext<DefaultTransaction> {
    public DbContext(bool startPaused = false) : base(startPaused) { }
    public DbContext(ColdStore cold, bool startPaused = false) : base(cold, startPaused) { }

    public ValueTask<Result> Run(Func<DbContext, Result> func, PropagationMode mode = PropagationMode.Optimistic)
        => base.Run(static (ctx, _, f) => f((DbContext)ctx), func, mode);

    public ValueTask<Result<T>> Run<T>(Func<DbContext, Result<T>> func, PropagationMode mode = PropagationMode.Optimistic)
        => base.Run<T, Func<DbContext, Result<T>>>(static (ctx, _, f) => f((DbContext)ctx), func, mode);

    public ValueTask<Result> Run<TArgs>(Func<DbContext, TArgs, Result> func, TArgs args, PropagationMode mode = PropagationMode.Optimistic)
        => base.Run<(Func<DbContext, TArgs, Result> Func, TArgs Args)>(
            static (ctx, _, state) => state.Func((DbContext)ctx, state.Args), (func, args), mode);

    public ValueTask<Result<T>> Run<T, TArgs>(Func<DbContext, TArgs, Result<T>> func, TArgs args, PropagationMode mode = PropagationMode.Optimistic)
        => base.Run<T, (Func<DbContext, TArgs, Result<T>> Func, TArgs Args)>(
            static (ctx, _, state) => state.Func((DbContext)ctx, state.Args), (func, args), mode);

    public ValueTask<Result> RunConfirmed(Func<DbContext, Result> func) => Run(func, PropagationMode.Confirmed);

    public ValueTask<Result<T>> RunConfirmed<T>(Func<DbContext, Result<T>> func) => Run(func, PropagationMode.Confirmed);

    public ValueTask<Result> RunConfirmed<TArgs>(Func<DbContext, TArgs, Result> func, TArgs args) => Run(func, args, PropagationMode.Confirmed);

    public ValueTask<Result<T>> RunConfirmed<T, TArgs>(Func<DbContext, TArgs, Result<T>> func, TArgs args) => Run(func, args, PropagationMode.Confirmed);
}
