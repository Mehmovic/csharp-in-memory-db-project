using RhinoDB.Lib.Hosting;
using RhinoDB.Lib.Realtime;

namespace RhinoDB.Lib.Execution;

public sealed class RhinoCtx {
    private readonly object? directDb;
    private readonly RhinoHost? host;

    public Identity Identity { get; }
    public Session? Session { get; }

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

    private TDb ResolveRoot<TDb>() where TDb : notnull =>
        directDb is not null ? (TDb)directDb : host!.GetDatabase<TDb>();

    private Task<Result<TChildDb>> ResolveChildAsync<TChildDb, TTx, TKey>(TKey key)
        where TChildDb : DbContext<TTx> where TTx : ITransaction where TKey : notnull {
        if (host is null)
            throw new InvalidOperationException(
                "Child database access needs a RhinoHost-backed RhinoCtx (e.g. a [Procedure]) - "
                + "not available from a lifecycle hook, which fires before/independent of the host.");
        return host.GetOrActivateChildAsync<TChildDb, TTx, TKey>(key);
    }
}
