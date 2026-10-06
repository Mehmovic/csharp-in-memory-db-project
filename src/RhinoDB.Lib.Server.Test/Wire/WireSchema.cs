using RhinoDB.Core;
using RhinoDB.Core.Procedures;
using RhinoDB.Core.Tables;
using RhinoDB.Lib.Execution;

namespace RhinoDB.Lib.Server.Test.Wire;

[Database]
public partial class WireDb : DbContext<WireDbTransaction> { }

[Table<WireDb>(TableKind.Instant)]
public readonly partial record struct Counter([PrimaryKey] int Id, int Value);

static public class WireProcedures {
    [Procedure]
    static public Result Add(WireDbTxCtx ctx, int counterId, int amount) {
        var current = ctx.Tx.Instant.Counter.Primary.Find(counterId);
        var value = (current.HasRow() ? current.Get().Unwrap().Value : 0) + amount;
        if (current.HasRow()) ctx.Tx.Instant.Counter.Delete(counterId);
        ctx.Tx.Instant.Counter.Insert(new Counter(counterId, value));
        return Result.Ok();
    }

    [Procedure]
    static public Task<Result<int>> Get(RhinoCtx ctx, int counterId) =>
        ctx.BeginTx(static (db, tx, id) => {
            var counter = tx.Instant.Counter.Primary.Find(id);
            return Result.Ok(counter.HasRow() ? counter.Get().Unwrap().Value : 0);
        }, counterId);

    [Procedure]
    static public Task<Result<uint>> AppVersion(RhinoCtx ctx) => Task.FromResult(Result.Ok(ctx.Session!.AppVersion));

    [Procedure]
    static public Task<Result> Reject(RhinoCtx ctx, ushort code) => Task.FromResult(Result.Error(DbError.Custom(code)));

    [Procedure]
    static public Task<Result> Crash(RhinoCtx ctx) => throw new InvalidOperationException("secret internals that must never reach a client");

    [Procedure]
    static public Task<Result> EngineDown(RhinoCtx ctx) => Task.FromResult(Result.Error(DbError.WalDurabilityFailed()));
}
