namespace RhinoDB.Lib.Execution;

// Declares which databases a locked multi-database transaction will touch - all of them are held from
// LockMultiTx on, so they have to be known up front (holding them in the fixed global order is what keeps two
// open transactions from deadlocking each other).
public sealed class MultiTxParticipants {
    private readonly MultiTxParticipantSet set;

    internal MultiTxParticipants(MultiTxParticipantSet set) => this.set = set;

    public MultiTxParticipants Root<TDb, TTx>() where TDb : DbContext<TTx> where TTx : ITransaction {
        set.Root<TDb, TTx>();
        return this;
    }

    public MultiTxParticipants Child<TChildDb, TTx, TKey>(TKey key)
        where TChildDb : DbContext<TTx> where TTx : ITransaction where TKey : notnull {
        set.Child<TChildDb, TTx, TKey>(key);
        return this;
    }
}

// Locks every declared database now and keeps it locked until Commit,
// Rollback or disposal, and each Run executes immediately and hands its value straight back - so plain code
// (I/O included) can run between steps. The cost is exactly that hold: nothing else reaches those databases until
// this transaction ends. A failed step aborts the whole transaction on the spot and releases everything.
public sealed class LockedMultiTx : IAsyncDisposable {
    private enum State {
        Opening,
        Open,
        Committed,
        RolledBack,
        Failed,
    }

    private readonly RhinoCtx ctx;
    private readonly List<MultiTxParticipant> held = [];
    private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
    private State state = State.Opening;
    private Result failure = Result.Ok();

    #if DEBUG
    public MultiTxCrashPoint? TestOnlySimulateCrashAt { get; set; }
    #endif

    internal LockedMultiTx(RhinoCtx ctx) {
        this.ctx = ctx;
        Participants = new MultiTxParticipantSet(ctx);
    }

    internal MultiTxParticipantSet Participants { get; }

    internal async Task<Result<LockedMultiTx>> OpenAsync() {
        var acquired = await Participants.AcquireAllAsync(held);
        if (acquired.IsError()) {
            state = State.Failed;
            failure = acquired;
            return Result<LockedMultiTx>.Error(acquired.GetError());
        }
        state = State.Open;
        return this;
    }

    public Task<Result> Run<TDb, TTx>(Func<TDb, TTx, Result> body)
        where TDb : DbContext<TTx> where TTx : ITransaction =>
        RunAsync(Participants.Find<TDb, TTx>(null), new MultiTxStep<TDb, TTx>(body));

    public Task<Result> Run<TDb, TTx, TArgs>(Func<TDb, TTx, TArgs, Result> body, TArgs args)
        where TDb : DbContext<TTx> where TTx : ITransaction =>
        RunAsync(Participants.Find<TDb, TTx>(null), new MultiTxStep<TDb, TTx, TArgs>(body, args));

    public Task<Result> Run<TChildDb, TTx, TKey>(TKey key, Func<TChildDb, TTx, Result> body)
        where TChildDb : DbContext<TTx> where TTx : ITransaction where TKey : notnull =>
        RunAsync(Participants.Find<TChildDb, TTx>(key), new MultiTxStep<TChildDb, TTx>(body));

    public Task<Result> Run<TChildDb, TTx, TKey, TArgs>(TKey key, Func<TChildDb, TTx, TArgs, Result> body, TArgs args)
        where TChildDb : DbContext<TTx> where TTx : ITransaction where TKey : notnull =>
        RunAsync(Participants.Find<TChildDb, TTx>(key), new MultiTxStep<TChildDb, TTx, TArgs>(body, args));

    public Task<Result<T>> Run<TDb, TTx, T>(Func<TDb, TTx, Result<T>> body)
        where TDb : DbContext<TTx> where TTx : ITransaction =>
        RunValueAsync(Participants.Find<TDb, TTx>(null), static (db, tx, state) => state(db, tx), body);

    public Task<Result<T>> Run<TDb, TTx, TArgs, T>(Func<TDb, TTx, TArgs, Result<T>> body, TArgs args)
        where TDb : DbContext<TTx> where TTx : ITransaction =>
        RunValueAsync(Participants.Find<TDb, TTx>(null), static (db, tx, state) => state.Body(db, tx, state.Args), (Body: body, Args: args));

    public Task<Result<T>> Run<TChildDb, TTx, TKey, T>(TKey key, Func<TChildDb, TTx, Result<T>> body)
        where TChildDb : DbContext<TTx> where TTx : ITransaction where TKey : notnull =>
        RunValueAsync(Participants.Find<TChildDb, TTx>(key), static (db, tx, state) => state(db, tx), body);

    public Task<Result<T>> Run<TChildDb, TTx, TKey, TArgs, T>(TKey key, Func<TChildDb, TTx, TArgs, Result<T>> body, TArgs args)
        where TChildDb : DbContext<TTx> where TTx : ITransaction where TKey : notnull =>
        RunValueAsync(Participants.Find<TChildDb, TTx>(key), static (db, tx, state) => state.Body(db, tx, state.Args), (Body: body, Args: args));

    public async Task<Result> Commit(PropagationMode mode = PropagationMode.Optimistic) {
        await gate.WaitAsync();
        try {
            if (EnsureUsable() is { } unusable) return unusable;

            #if DEBUG
            var crashAt = TestOnlySimulateCrashAt;
            #else
            MultiTxCrashPoint? crashAt = null;
            #endif
            var committed = await MultiTxCommit.ApplyAndCommitAsync(ctx, held, mode, crashAt);
            if (committed.IsError()) {
                state = State.Failed;
                failure = committed;
                return committed;
            }
            state = State.Committed;
            return committed;
        } finally {
            gate.Release();
        }
    }

    // Releases every held database without applying anything. A no-op once the transaction has already ended.
    public async Task Rollback() {
        await gate.WaitAsync();
        try {
            if (state != State.Open) return;
            await MultiTxCommit.AbortAllAsync(held, Result.Ok());
            state = State.RolledBack;
        } finally {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync() => await Rollback();

    private async Task<Result> RunAsync<TDb, TTx>(MultiTxParticipant<TDb, TTx>? participant, MultiTxStep<TDb, TTx> step)
        where TDb : DbContext<TTx> where TTx : ITransaction {
        await gate.WaitAsync();
        try {
            if (EnsureUsable() is { } unusable) return unusable;

            var ran = participant is null
                ? Result.Error(DbError.MultiTxParticipantNotDeclared())
                : await participant.RunStepAsync(participant.AddStep(step));
            if (ran.IsError()) {
                // Whatever the body staged before failing must never be committed - end everything now.
                await MultiTxCommit.AbortAllAsync(held, ran);
                state = State.Failed;
                failure = ran;
            }
            return ran;
        } finally {
            gate.Release();
        }
    }

    private async Task<Result<T>> RunValueAsync<TDb, TTx, TState, T>(
        MultiTxParticipant<TDb, TTx>? participant, Func<TDb, TTx, TState, Result<T>> invoke, TState state)
        where TDb : DbContext<TTx> where TTx : ITransaction {
        var value = new TxValue<T>();
        var ran = await RunAsync(participant, new MultiTxValueStep<TDb, TTx, TState, T>(invoke, state, value));
        return ran.IsError() ? Result<T>.Error(ran.GetError()) : Result<T>.Ok(value.Value);
    }

    private Result? EnsureUsable() => state switch {
        State.Open => null,
        State.Failed => failure,
        _ => Result.Error(DbError.MultiTxAlreadyFinished()),
    };
}
