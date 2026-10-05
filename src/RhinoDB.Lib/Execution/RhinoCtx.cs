using RhinoDB.Lib.Hosting;
using RhinoDB.Lib.Realtime;

namespace RhinoDB.Lib.Execution;

public sealed class RhinoCtx {
    static private readonly InvalidOperationException InvalidServerVersionException = new InvalidOperationException(
        "Server version needs a RhinoHost-backed RhinoCtx (e.g. a [Procedure]) - not available from a lifecycle hook, which fires before/independent of the host.");
    
    private readonly object? directDb;
    private readonly RhinoHost? host;
    private uint? serverVersion;

    public Identity Identity { get; }
    public Session? Session { get; }

    public uint ServerVersion => serverVersion ??= host?.ServerVersion ?? throw InvalidServerVersionException;

    internal RhinoHost? Host => host;
    
    public RhinoCtx(object directDb, Identity identity, Session? session = null) {
        this.directDb = directDb;
        Identity = identity;
        Session = session;
    }

    public RhinoCtx(RhinoHost host, Identity identity, Session? session = null) {
        this.host = host;
        Identity = identity;
        Session = session;
    }

    public Task<Result> BeginTx<TDb, TTx>(Func<TDb, TTx, Result> body) where TDb : DbContext<TTx> where TTx : ITransaction {
        var db = ResolveRoot<TDb>();
        return db.Run(static (ctx, tx, b) => b((TDb)ctx, tx), body).AsTask();
    }

    public Task<Result<T>> BeginTx<TDb, TTx, T>(Func<TDb, TTx, Result<T>> body) where TDb : DbContext<TTx> where TTx : ITransaction {
        var db = ResolveRoot<TDb>();
        return db.Run(static (ctx, tx, b) => b((TDb)ctx, tx), body).AsTask();
    }

    public async Task<Result> BeginTx<TChildDb, TTx, TKey>(TKey key, Func<TChildDb, TTx, Result> body)
        where TChildDb : DbContext<TTx> where TTx : ITransaction where TKey : notnull {
        var childResult = await ResolveChildAsync<TChildDb, TTx, TKey>(key);
        if (childResult.IsError()) return childResult.Void();
        return await childResult.Unwrap().Run(static (ctx, tx, b) => b((TChildDb)ctx, tx), body);
    }

    public async Task<Result<T>> BeginTx<TChildDb, TTx, TKey, T>(TKey key, Func<TChildDb, TTx, Result<T>> body)
        where TChildDb : DbContext<TTx> where TTx : ITransaction where TKey : notnull {
        var childResult = await ResolveChildAsync<TChildDb, TTx, TKey>(key);
        if (childResult.IsError()) return childResult.Void();
        return await childResult.Unwrap().Run(static (ctx, tx, b) => b((TChildDb)ctx, tx), body);
    }

    public PlannedMultiTx PlanMultiTx() => new PlannedMultiTx(this);

    public Task<Result<LockedMultiTx>> LockMultiTx(Action<MultiTxParticipants> declare) {
        var transaction = new LockedMultiTx(this);
        declare(new MultiTxParticipants(transaction.Participants));
        return transaction.Participants.Count == 0
            ? Task.FromResult(Result<LockedMultiTx>.Error(DbError.MultiTxNoParticipantsDeclared()))
            : transaction.OpenAsync();
    }

    internal TDb ResolveRootForMultiTx<TDb>() where TDb : notnull => ResolveRoot<TDb>();

    private TDb ResolveRoot<TDb>() where TDb : notnull =>
        directDb is not null ? (TDb)directDb : host!.GetDatabase<TDb>();

    private Task<Result<TChildDb>> ResolveChildAsync<TChildDb, TTx, TKey>(TKey key)
        where TChildDb : DbContext<TTx> where TTx : ITransaction where TKey : notnull {
        return host is null
            ? Task.FromResult(Result<TChildDb>.Error(DbError.ChildDatabaseRequiresHost()))
            : host.GetOrActivateChildAsync<TChildDb, TTx, TKey>(key);
    }
}
