using System.Threading.Channels;

using RhinoDB.Core.Exceptions;
using RhinoDB.Lib.Durability;
using RhinoDB.Lib.Hosting;

namespace RhinoDB.Lib.Execution;

#if DEBUG
public
#else
internal
#endif
enum MultiTxCrashPoint {
    AfterFirstDurablePrepare,
    AfterAllDurablePrepares,
}

// One transaction across the Root and any number of Children, planned up front: Add only records bodies; Commit holds
// every participant in the fixed global order, runs the bodies in Add order, then applies.
public sealed class PlannedMultiTx {
    private readonly RhinoCtx ctx;
    private readonly MultiTxParticipantSet participants;
    private readonly List<(MultiTxParticipant Participant, int StepIndex)> stepsInAddOrder = [];
    private readonly List<ITxValue> values = [];
    private bool finished;

    #if DEBUG
    public MultiTxCrashPoint? TestOnlySimulateCrashAt { get; set; }
    #endif

    internal PlannedMultiTx(RhinoCtx ctx) {
        this.ctx = ctx;
        participants = new MultiTxParticipantSet(ctx);
    }

    public int ParticipantCount => participants.Count;

    public PlannedMultiTx Add<TDb, TTx>(Func<TDb, TTx, Result> body)
        where TDb : DbContext<TTx> where TTx : ITransaction =>
        AddStep(Root<TDb, TTx>(), new MultiTxStep<TDb, TTx>(body));

    public PlannedMultiTx Add<TDb, TTx, TArgs>(Func<TDb, TTx, TArgs, Result> body, TArgs args)
        where TDb : DbContext<TTx> where TTx : ITransaction =>
        AddStep(Root<TDb, TTx>(), new MultiTxStep<TDb, TTx, TArgs>(body, args));

    public PlannedMultiTx Add<TChildDb, TTx, TKey>(TKey key, Func<TChildDb, TTx, Result> body)
        where TChildDb : DbContext<TTx> where TTx : ITransaction where TKey : notnull =>
        AddStep(Child<TChildDb, TTx, TKey>(key), new MultiTxStep<TChildDb, TTx>(body));

    public PlannedMultiTx Add<TChildDb, TTx, TKey, TArgs>(TKey key, Func<TChildDb, TTx, TArgs, Result> body, TArgs args)
        where TChildDb : DbContext<TTx> where TTx : ITransaction where TKey : notnull =>
        AddStep(Child<TChildDb, TTx, TKey>(key), new MultiTxStep<TChildDb, TTx, TArgs>(body, args));

    public TxValue<T> Add<TDb, TTx, T>(Func<TDb, TTx, Result<T>> body)
        where TDb : DbContext<TTx> where TTx : ITransaction =>
        AddValueStep(Root<TDb, TTx>(), static (db, tx, state) => state(db, tx), body);

    public TxValue<T> Add<TDb, TTx, TArgs, T>(Func<TDb, TTx, TArgs, Result<T>> body, TArgs args)
        where TDb : DbContext<TTx> where TTx : ITransaction =>
        AddValueStep(Root<TDb, TTx>(), static (db, tx, state) => state.Body(db, tx, state.Args), (Body: body, Args: args));

    public TxValue<T> Add<TChildDb, TTx, TKey, T>(TKey key, Func<TChildDb, TTx, Result<T>> body)
        where TChildDb : DbContext<TTx> where TTx : ITransaction where TKey : notnull =>
        AddValueStep(Child<TChildDb, TTx, TKey>(key), static (db, tx, state) => state(db, tx), body);

    public TxValue<T> Add<TChildDb, TTx, TKey, TArgs, T>(TKey key, Func<TChildDb, TTx, TArgs, Result<T>> body, TArgs args)
        where TChildDb : DbContext<TTx> where TTx : ITransaction where TKey : notnull =>
        AddValueStep(Child<TChildDb, TTx, TKey>(key), static (db, tx, state) => state.Body(db, tx, state.Args), (Body: body, Args: args));

    public void Rollback() {
        EnsureOpen();
        finished = true;
        participants.Clear();
        stepsInAddOrder.Clear();
        RevokeValues();
    }

    public async Task<Result> Commit(PropagationMode mode = PropagationMode.Optimistic) {
        if (finished) return Result.Error(DbError.MultiTxAlreadyFinished());
        finished = true;
        if (stepsInAddOrder.Count == 0) return Result.Ok();

        var held = new List<MultiTxParticipant>(participants.Count);
        try {
            var acquired = await participants.AcquireAllAsync(held);
            if (acquired.IsError()) return Fail(acquired);

            foreach (var (participant, stepIndex) in stepsInAddOrder) {
                var ran = await participant.RunStepAsync(stepIndex);
                if (ran.IsError()) return Fail(await MultiTxCommit.AbortAllAsync(held, ran));
            }

            #if DEBUG
            var crashAt = TestOnlySimulateCrashAt;
            #else
            MultiTxCrashPoint? crashAt = null;
            #endif
            var committed = await MultiTxCommit.ApplyAndCommitAsync(ctx, held, mode, crashAt);
            if (committed.IsError()) return Fail(committed);

            foreach (var value in values) value.Commit();
            return committed;
        } catch (Exception ex) {
            return Fail(await MultiTxCommit.AbortAllAsync(held.Where(p => !p.IsFinished), Result.Error(ex)));
        }
    }

    private Result Fail(Result failure) {
        RevokeValues();
        return failure;
    }

    private void RevokeValues() {
        foreach (var value in values) value.Revoke();
    }

    private PlannedMultiTx AddStep<TDb, TTx>(MultiTxParticipant<TDb, TTx> participant, MultiTxStep<TDb, TTx> step)
        where TDb : DbContext<TTx> where TTx : ITransaction {
        stepsInAddOrder.Add((participant, participant.AddStep(step)));
        return this;
    }

    private TxValue<T> AddValueStep<TDb, TTx, TState, T>(
        MultiTxParticipant<TDb, TTx> participant, Func<TDb, TTx, TState, Result<T>> invoke, TState state)
        where TDb : DbContext<TTx> where TTx : ITransaction {
        var value = new TxValue<T>();
        AddStep(participant, new MultiTxValueStep<TDb, TTx, TState, T>(invoke, state, value));
        values.Add(value);
        return value;
    }

    private MultiTxParticipant<TDb, TTx> Root<TDb, TTx>() where TDb : DbContext<TTx> where TTx : ITransaction {
        EnsureOpen();
        return participants.Root<TDb, TTx>();
    }

    private MultiTxParticipant<TChildDb, TTx> Child<TChildDb, TTx, TKey>(TKey key)
        where TChildDb : DbContext<TTx> where TTx : ITransaction where TKey : notnull {
        EnsureOpen();
        if (ctx.Host is null) throw new ChildDatabaseRequiresHostException();
        return participants.Child<TChildDb, TTx, TKey>(key);
    }

    private void EnsureOpen() {
        if (finished) throw new InvalidOperationException(
            "This multi-database transaction was already committed or rolled back - it is single-use, begin a new one.");
    }
}

// The participants of one multi-database transaction (planned or locked), one per database.
internal sealed class MultiTxParticipantSet(RhinoCtx ctx) {
    private readonly Dictionary<(Type DbType, object? Key), MultiTxParticipant> byKey = [];
    private readonly List<MultiTxParticipant> inAddOrder = [];

    public int Count => byKey.Count;

    public IReadOnlyList<MultiTxParticipant> All => inAddOrder;

    public MultiTxParticipant<TDb, TTx> Root<TDb, TTx>() where TDb : DbContext<TTx> where TTx : ITransaction {
        var groupKey = (typeof(TDb), (object?)null);
        if (byKey.TryGetValue(groupKey, out var existing)) return (MultiTxParticipant<TDb, TTx>)existing;

        var participant = new MultiTxParticipant<TDb, TTx>(
            () => Task.FromResult(Result<(TDb, string)>.Ok((ctx.ResolveRootForMultiTx<TDb>(), ChainLog.RootParticipantId))));
        byKey[groupKey] = participant;
        inAddOrder.Add(participant);
        return participant;
    }

    public MultiTxParticipant<TChildDb, TTx> Child<TChildDb, TTx, TKey>(TKey key)
        where TChildDb : DbContext<TTx> where TTx : ITransaction where TKey : notnull {
        var groupKey = (typeof(TChildDb), (object?)key);
        if (byKey.TryGetValue(groupKey, out var existing)) return (MultiTxParticipant<TChildDb, TTx>)existing;

        var host = ctx.Host;
        var participant = new MultiTxParticipant<TChildDb, TTx>(async () => {
            // No host (a lifecycle hook's ctx): reported at activation, as an error, never thrown from here.
            if (host is null) return Result<(TChildDb, string)>.Error(DbError.ChildDatabaseRequiresHost());
            var child = await host.GetOrActivateChildAsync<TChildDb, TTx, TKey>(key);
            if (child.IsError()) return Result<(TChildDb, string)>.Error(child.GetError());
            return Result<(TChildDb, string)>.Ok((child.Unwrap(), host.ChildParticipantId<TChildDb>(key)));
        });
        byKey[groupKey] = participant;
        inAddOrder.Add(participant);
        return participant;
    }

    public MultiTxParticipant<TDb, TTx>? Find<TDb, TTx>(object? key) where TDb : DbContext<TTx> where TTx : ITransaction =>
        byKey.TryGetValue((typeof(TDb), key), out var participant) ? (MultiTxParticipant<TDb, TTx>)participant : null;

    public void Clear() {
        byKey.Clear();
        inAddOrder.Clear();
    }

    public async Task<Result> AcquireAllAsync(List<MultiTxParticipant> held) {
        foreach (var participant in inAddOrder) {
            var activated = await participant.ActivateAsync();
            if (activated.IsError()) return activated;
        }

        foreach (var participant in inAddOrder.OrderBy(p => p.Id, StringComparer.Ordinal)) {
            var acquired = await participant.AcquireAsync();
            if (acquired.IsError()) return await MultiTxCommit.AbortAllAsync(held, acquired);
            held.Add(participant);
        }
        return Result.Ok();
    }
}

// Apply + durability for a set of held participants - shared by both transaction kinds.
static internal class MultiTxCommit {
    static public async Task<Result> ApplyAndCommitAsync(
        RhinoCtx ctx, List<MultiTxParticipant> held, PropagationMode mode, MultiTxCrashPoint? crashAt) {
        foreach (var participant in held) {
            var applied = await participant.ApplyAsync();
            if (applied.IsError()) return await AbortAllAsync(held, applied);
        }

        var durable = held.Where(p => p.HasDurableChanges).ToList();
        return durable.Count <= 1
            ? await CommitWithoutChainLogAsync(held, durable, mode)
            : await CommitTwoPhaseAsync(ctx, held, durable, crashAt);
    }

    static public async Task<Result> AbortAllAsync(IEnumerable<MultiTxParticipant> held, Result failure) {
        foreach (var participant in held) {
            try { await participant.AbortAsync(); } catch { /* the participant poisons itself if it can't revert */ }
        }
        return failure;
    }

    static private async Task<Result> CommitWithoutChainLogAsync(List<MultiTxParticipant> held, List<MultiTxParticipant> durable, PropagationMode mode) {
        var outcome = durable.Count == 1 ? await durable[0].CommitPlainAsync(mode) : Result.Ok();

        foreach (var participant in held.Where(p => !p.IsFinished)) {
            if (outcome.IsOk()) await participant.CommitPlainAsync(mode);
            else await participant.AbortAsync();
        }
        return outcome;
    }

    static private async Task<Result> CommitTwoPhaseAsync(
        RhinoCtx ctx, List<MultiTxParticipant> held, List<MultiTxParticipant> durable, MultiTxCrashPoint? crashAt) {
        var chainId = Guid.NewGuid();
        var ids = durable.Select(p => p.Id).ToArray();

        #if DEBUG
        if (crashAt == MultiTxCrashPoint.AfterFirstDurablePrepare) {
            await durable[0].PrepareDurableAsync(chainId, ids);
            return await SimulateCrashAsync(held);
        }
        #endif

        var prepared = await Task.WhenAll(durable.Select(p => p.PrepareDurableAsync(chainId, ids)));
        var failedPrepare = prepared.FirstOrDefault(r => r.IsError());
        if (failedPrepare.IsError()) {
            var recorded = ctx.Host?.ChainLog?.Record(chainId, commit: false, ids)
                ?? Result<bool>.Error(DbError.ChainResolutionUnavailable());
            if (recorded.IsError()) {
                var unknown = DbError.MultiTxOutcomeUnknown(recorded.GetError().ToException());
                foreach (var participant in held) participant.Poison(unknown);
                return await AbortAllAsync(held, Result.Error(unknown));
            }
            return await AbortAllAsync(held, failedPrepare);
        }

        #if DEBUG
        if (crashAt == MultiTxCrashPoint.AfterAllDurablePrepares) return await SimulateCrashAsync(held);
        #endif

        // Every prepare is durable: committed, whatever happens next - the markers are a recovery shortcut only.
        foreach (var participant in held) {
            if (participant.HasDurableChanges) await participant.CommitPreparedAsync();
            else await participant.CommitPlainAsync(PropagationMode.Optimistic);
        }
        return Result.Ok();
    }

    #if DEBUG
    static private async Task<Result> SimulateCrashAsync(List<MultiTxParticipant> held) {
        foreach (var participant in held) await participant.AbandonAsync();
        return Result.Error(DbError.SystemFailure(new InvalidOperationException("Simulated crash (TestOnlySimulateCrashAt).")));
    }
    #endif
}

internal interface ITxValue {
    void Commit();
    void Revoke();
}

// Readable inside the transaction by any body added after its producer, and afterwards only if Commit succeeded.
public sealed class TxValue<T> : ITxValue {
    private enum State {
        Pending,
        Produced,
        Committed,
        Revoked,
    }

    private T value = default!;
    private volatile State state;

    public bool HasValue => state is State.Produced or State.Committed;

    public T Value => state switch {
        State.Produced or State.Committed => value,
        State.Pending => throw new MultiTxValueNotProducedException(),
        _ => throw new InvalidOperationException(
            "This value's multi-database transaction failed or was rolled back - its body's writes were reverted, so the value isn't real."),
    };

    internal void Produce(T produced) {
        value = produced;
        state = State.Produced;
    }

    void ITxValue.Commit() => state = State.Committed;

    void ITxValue.Revoke() {
        value = default!;
        state = State.Revoked;
    }
}

internal class MultiTxStep<TDb, TTx>(Func<TDb, TTx, Result>? body = null) where TDb : DbContext<TTx> where TTx : ITransaction {
    public virtual Result Invoke(TDb db, TTx tx) => body!(db, tx);
}

internal sealed class MultiTxStep<TDb, TTx, TArgs>(Func<TDb, TTx, TArgs, Result> body, TArgs args) : MultiTxStep<TDb, TTx>
    where TDb : DbContext<TTx> where TTx : ITransaction {
    public override Result Invoke(TDb db, TTx tx) => body(db, tx, args);
}

internal sealed class MultiTxValueStep<TDb, TTx, TState, T>(Func<TDb, TTx, TState, Result<T>> invoke, TState state, TxValue<T> sink)
    : MultiTxStep<TDb, TTx>
    where TDb : DbContext<TTx> where TTx : ITransaction {
    public override Result Invoke(TDb db, TTx tx) {
        var result = invoke(db, tx, state);
        if (result.IsError()) return Result.Error(result.GetError());
        sink.Produce(result.Unwrap());
        return Result.Ok();
    }
}

internal abstract class MultiTxParticipant {
    public string Id { get; protected set; } = "";
    public bool IsFinished { get; protected set; }
    public abstract bool HasDurableChanges { get; }
    public abstract Task<Result> ActivateAsync();
    public abstract Task<Result> AcquireAsync();
    public abstract Task<Result> RunStepAsync(int stepIndex);
    public abstract Task<Result> ApplyAsync();
    public abstract Task<Result> CommitPlainAsync(PropagationMode mode);
    public abstract Task<Result> PrepareDurableAsync(Guid chainId, string[] participantIds);
    public abstract Task<Result> CommitPreparedAsync();
    public abstract Task<Result> AbortAsync();
    #if DEBUG
    public abstract Task<Result> AbandonAsync();
    #endif
    public abstract void Poison(DbError error);
}

internal sealed class MultiTxParticipant<TDb, TTx>(Func<Task<Result<(TDb Db, string Id)>>> resolve) : MultiTxParticipant
    where TDb : DbContext<TTx> where TTx : ITransaction {
    private readonly List<MultiTxStep<TDb, TTx>> steps = [];
    private TDb? db;
    private MultiTxParticipantWorkItem<TDb, TTx>? item;

    public override bool HasDurableChanges => item?.HasDurableChanges ?? false;

    public int AddStep(MultiTxStep<TDb, TTx> step) {
        steps.Add(step);
        return steps.Count - 1;
    }

    public override async Task<Result> ActivateAsync() {
        var resolved = await resolve();
        if (resolved.IsError()) return resolved.Void();
        (db, Id) = resolved.Unwrap();
        return Result.Ok();
    }

    public override Task<Result> AcquireAsync() {
        item = new MultiTxParticipantWorkItem<TDb, TTx>(db!, steps);
        if (db!.TryEnqueueWorkItem(item)) return item.Held;
        IsFinished = true;
        return Task.FromResult(Result.Error(DbError.DatabaseClosing()));
    }

    public override Task<Result> RunStepAsync(int stepIndex) => Send(MultiTxInstruction.RunStep(stepIndex), finishes: false);

    public override Task<Result> ApplyAsync() => Send(MultiTxInstruction.Of(MultiTxCommand.Apply), finishes: false);

    public override Task<Result> CommitPlainAsync(PropagationMode mode) => Send(MultiTxInstruction.CommitPlain(mode), finishes: true);

    public override Task<Result> PrepareDurableAsync(Guid chainId, string[] participantIds) =>
        Send(MultiTxInstruction.PrepareDurable(chainId, participantIds), finishes: false);

    public override Task<Result> CommitPreparedAsync() => Send(MultiTxInstruction.Of(MultiTxCommand.CommitPrepared), finishes: true);

    public override Task<Result> AbortAsync() => Send(MultiTxInstruction.Of(MultiTxCommand.Abort), finishes: true);

    #if DEBUG
    public override Task<Result> AbandonAsync() => Send(MultiTxInstruction.Of(MultiTxCommand.Abandon), finishes: true);
    #endif

    public override void Poison(DbError error) => db?.PoisonDatabase(error);

    private Task<Result> Send(MultiTxInstruction instruction, bool finishes) {
        if (IsFinished || item is null) return Task.FromResult(Result.Ok());
        if (finishes) IsFinished = true;
        item.Instructions.Writer.TryWrite(instruction);
        return instruction.Reply.Task;
    }
}

internal enum MultiTxCommand {
    RunStep,
    Apply,
    CommitPlain,
    PrepareDurable,
    CommitPrepared,
    Abort,
    #if DEBUG
    Abandon,
    #endif
}

internal sealed class MultiTxInstruction {
    public MultiTxCommand Command { get; private init; }
    public int StepIndex { get; private init; }
    public PropagationMode Mode { get; private init; }
    public Guid ChainId { get; private init; }
    public string[] ParticipantIds { get; private init; } = [];
    public TaskCompletionSource<Result> Reply { get; } = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);

    static public MultiTxInstruction Of(MultiTxCommand command) => new MultiTxInstruction { Command = command };

    static public MultiTxInstruction RunStep(int stepIndex) => new MultiTxInstruction { Command = MultiTxCommand.RunStep, StepIndex = stepIndex };

    static public MultiTxInstruction CommitPlain(PropagationMode mode) =>
        new MultiTxInstruction { Command = MultiTxCommand.CommitPlain, Mode = mode };

    static public MultiTxInstruction PrepareDurable(Guid chainId, string[] participantIds) =>
        new MultiTxInstruction { Command = MultiTxCommand.PrepareDurable, ChainId = chainId, ParticipantIds = participantIds };
}

// Holds the participant's loop from acquisition until the decision; every body and the apply run here, on it.
internal sealed class MultiTxParticipantWorkItem<TDb, TTx>(TDb db, List<MultiTxStep<TDb, TTx>> steps) : IAsyncExecutionWorkItem
    where TDb : DbContext<TTx> where TTx : ITransaction {
    private readonly TaskCompletionSource<Result> held = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);

    public Channel<MultiTxInstruction> Instructions { get; } = Channel.CreateUnbounded<MultiTxInstruction>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    public Task<Result> Held => held.Task;

    public bool HasDurableChanges { get; private set; }

    void IExecutionWorkItem.Run() => RunAsync().GetAwaiter().GetResult();

    public async Task RunAsync() {
        var cold = db.Cold;
        if (db.Poison is { } poisoned) {
            held.TrySetResult(Result.Error(poisoned));
            return;
        }

        cold?.BeginScope();
        TTx tx;
        try {
            tx = db.CreateTransaction();
        } catch (Exception ex) {
            cold?.EndScope(commit: false, PropagationMode.Optimistic, 0);
            held.TrySetResult(Result.Error(ex));
            return;
        }
        held.TrySetResult(Result.Ok());

        var applied = false;
        var durablyPrepared = false;
        var chainId = Guid.Empty;
        while (true) {
            var instruction = await Instructions.Reader.ReadAsync();
            switch (instruction.Command) {
                case MultiTxCommand.RunStep: {
                    // Applied right away, so the transaction's later steps on this database see it; the lock
                    // keeps everyone else out until the decision, and the undo journal covers every step.
                    var result = RunStep(instruction.StepIndex, tx);
                    if (result.IsOk()) result = Apply(tx, ref applied);
                    instruction.Reply.TrySetResult(result);
                    continue;
                }
                case MultiTxCommand.Apply: {
                    instruction.Reply.TrySetResult(Apply(tx, ref applied));
                    continue;
                }
                case MultiTxCommand.PrepareDurable: {
                    chainId = instruction.ChainId;
                    var error = await cold!.AppendChainPrepare(tx.LastLsn ?? 0, chainId, instruction.ParticipantIds);
                    if (error is { } durabilityError) db.PoisonDatabase(durabilityError);
                    else durablyPrepared = true;
                    instruction.Reply.TrySetResult(error is { } failed ? Result.Error(failed) : Result.Ok());
                    continue;
                }
                case MultiTxCommand.CommitPlain: {
                    tx.ReleaseRetainedUndo();
                    var durability = cold?.EndScope(commit: true, instruction.Mode, tx.LastLsn ?? 0) ?? Task.FromResult<DbError?>(null);
                    SweepIfDue(tx);
                    _ = ForwardDurabilityAsync(durability, instruction.Reply);
                    return;
                }
                case MultiTxCommand.CommitPrepared: {
                    tx.ReleaseRetainedUndo();
                    _ = cold!.EndChainScope(chainId, commit: true);
                    SweepIfDue(tx);
                    instruction.Reply.TrySetResult(Result.Ok());
                    return;
                }
                case MultiTxCommand.Abort: {
                    Undo(tx, applied);
                    if (durablyPrepared) _ = cold!.EndChainScope(chainId, commit: false);
                    else cold?.EndScope(commit: false, PropagationMode.Optimistic, 0);
                    instruction.Reply.TrySetResult(Result.Ok());
                    return;
                }
                #if DEBUG
                case MultiTxCommand.Abandon: {
                    // Simulated process death: nothing more reaches the WAL.
                    Undo(tx, applied);
                    cold?.EndScope(commit: false, PropagationMode.Optimistic, 0);
                    instruction.Reply.TrySetResult(Result.Ok());
                    return;
                }
                #endif
            }
        }
    }

    private Result Apply(TTx tx, ref bool applied) {
        Result result;
        try {
            result = tx.ApplyRetainingUndo();
        } catch (ApplyFailedException ex) {
            var error = DbError.ApplyFailed(ex.InnerException);
            db.PoisonDatabase(error);
            result = Result.Error(error);
        }
        if (result.IsOk()) {
            applied = true;
            HasDurableChanges = db.Cold?.HasStagedChanges ?? false;
        }
        return result;
    }

    private Result RunStep(int stepIndex, TTx tx) {
        try {
            return steps[stepIndex].Invoke(db, tx);
        } catch (MultiTxValueNotProducedException) {
            return Result.Error(DbError.MultiTxValueNotProduced());
        } catch (Exception ex) {
            return Result.Error(ex);
        }
    }

    // Drop whatever is still only staged (a failed step's writes), then revert every applied step through the
    // retained undo journals.
    private void Undo(TTx tx, bool applied) {
        tx.Discard();
        if (!applied || tx.RevertRetainedUndo()) return;
        db.PoisonDatabase(DbError.ApplyFailed(new InvalidOperationException(
            "A multi-database transaction participant could not revert its applied share after the transaction aborted.")));
    }

    private void SweepIfDue(TTx tx) {
        if (!db.Cleanup.ShouldSweep(tx.PendingStorageOrphanCount)) return;
        try { tx.SweepDeleted(); } catch { /* garbage stays until the next sweep */ }
    }

    private async Task ForwardDurabilityAsync(Task<DbError?> durability, TaskCompletionSource<Result> reply) {
        var error = await durability;
        if (error is { } durabilityError) db.PoisonDatabase(durabilityError);
        reply.TrySetResult(error is { } failed ? Result.Error(failed) : Result.Ok());
    }
}
