using RhinoDB.Lib.Storage;
using RhinoDB.Lib.Cold;
using RhinoDB.Lib.Durability;
using RhinoDB.Lib.Realtime;

namespace RhinoDB.Lib.Execution;

internal interface IHostedDatabase {
    ColdStore? Cold { get; }
    Action<DbError>? OnPoisoned { set; }
}

public class DbContext<TTx> : IRhinoClientLifecycle, IHostedDatabase where TTx : ITransaction {
    private readonly DbExecutionLoop<TTx> executionLoop;
    private object? poison;

    public CleanupCollector Cleanup { get; private init; }
    internal ColdStore? Cold { get; }
    internal DbError? Poison => poison is DbError error ? error : null;
    public bool IsPoisoned => poison is not null;

    internal Action<DbError>? OnPoisoned { get; set; }

    ColdStore? IHostedDatabase.Cold => Cold;
    Action<DbError>? IHostedDatabase.OnPoisoned { set => OnPoisoned = value; }

    protected DbContext(bool startPaused = false, CleanupCollector? cleanupCollector = null) {
        executionLoop = new DbExecutionLoop<TTx>(this, startPaused);
        Cleanup = cleanupCollector ?? new CleanupCollector(CleanupTrigger.PerTime);
    }

    protected DbContext(ColdStore? cold, bool startPaused = false, CleanupCollector? cleanupCollector = null) {
        Cold = cold;
        executionLoop = new DbExecutionLoop<TTx>(this, startPaused);
        Cleanup = cleanupCollector ?? new CleanupCollector(CleanupTrigger.PerTime);
    }
    
    internal void PoisonDatabase(DbError error) {
        if (Interlocked.CompareExchange(ref poison, error, null) is not null) return;
        OnPoisoned?.Invoke(error);
    }

    protected internal void ResumeExecution() => executionLoop.Resume();

    internal void BeginDraining() => executionLoop.BeginDraining();

    internal Task DrainAsync() => executionLoop.DrainAsync();

    internal bool TryEnqueueWorkItem(IExecutionWorkItem item) => executionLoop.TryEnqueue(item);

    protected internal virtual TTx CreateTransaction() => default!;

    protected internal virtual ArchiveRetentionPolicy? ConfiguredArchiveRetention => null;

    protected internal virtual Task LoadFromColdAsync() => Task.CompletedTask;

    protected internal virtual Task<Result> OnInitAsync() => Task.FromResult(Result.Ok());

    protected internal virtual Task<Result> OnStartAsync() => Task.FromResult(Result.Ok());

    protected internal virtual Task<Result> OnClientConnectAsync(Session session) => Task.FromResult(Result.Ok());

    protected internal virtual Task<Result> OnClientDisconnectAsync(Session session) => Task.FromResult(Result.Ok());

    Task<Result> IRhinoClientLifecycle.OnClientConnectAsync(Session session) => OnClientConnectAsync(session);

    Task<Result> IRhinoClientLifecycle.OnClientDisconnectAsync(Session session) => OnClientDisconnectAsync(session);

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
