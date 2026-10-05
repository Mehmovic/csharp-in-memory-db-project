using BenchmarkDotNet.Attributes;
using RhinoDB.Core;
using RhinoDB.Lib.Execution;
using RhinoDB.Sandbox.Benchmark.Schema;

namespace RhinoDB.Sandbox.Benchmark.Benchmarks;

[MemoryDiagnoser]
public class MultiDatabaseTransactionBenchmarks {
    private const int Transactions = 1_000;
    private const int TwoPhaseTransactions = 100;
    private const int MatchKey = 1;

    private BenchHost bench = null!;
    private int nextWalletId;
    private int nextMatchId;
    private int nextSeatId;

    [GlobalSetup]
    public void Setup() {
        bench = BenchHost.Start(BenchHost.NewDirectory());
        bench.Match(MatchKey);
    }

    [GlobalCleanup]
    public void Cleanup() => bench.StopAndDelete();

    [Benchmark(Baseline = true, OperationsPerInvoke = Transactions)]
    public Task BeginTxRootNoOp() => Fan(Transactions, _ =>
        bench.Ctx.BeginTx(static (HostRootDb db, HostRootDbTransaction tx) => Result.Ok()));

    [Benchmark(OperationsPerInvoke = Transactions)]
    public Task BeginTxRootInsert() => Fan(Transactions, _ => {
        var id = Interlocked.Increment(ref nextWalletId);
        return bench.Ctx.BeginTx((db, tx) => { tx.HostWallet.Insert(new HostWallet(id, 1)); return Result.Ok(); });
    });

    [Benchmark(OperationsPerInvoke = Transactions)]
    public Task PlannedRootOnly() => Fan(Transactions, _ => bench.Ctx.PlanMultiTx()
        .Add(static (db, tx, id) => { tx.HostWallet.Insert(new HostWallet(id, 1)); return Result.Ok(); }, Interlocked.Increment(ref nextWalletId))
        .Add(static (db, tx, id) => { tx.HostWallet.Insert(new HostWallet(id, 2)); return Result.Ok(); }, Interlocked.Increment(ref nextWalletId))
        .Commit());

    [Benchmark(OperationsPerInvoke = Transactions)]
    public Task PlannedRootAndInstantSingleton() => Fan(Transactions, _ => bench.Ctx.PlanMultiTx()
        .Add(static (db, tx, id) => { tx.HostWallet.Insert(new HostWallet(id, 1)); return Result.Ok(); }, Interlocked.Increment(ref nextWalletId))
        .Add(static (db, tx, id) => { tx.Instant.LobbySeat.Insert(new LobbySeat(id, id)); return Result.Ok(); }, Interlocked.Increment(ref nextSeatId))
        .Commit());

    [Benchmark(OperationsPerInvoke = TwoPhaseTransactions)]
    public Task PlannedRootAndChildTwoPhase() => Fan(TwoPhaseTransactions, _ => bench.Ctx.PlanMultiTx()
        .Add(static (db, tx, id) => { tx.HostWallet.Insert(new HostWallet(id, 1)); return Result.Ok(); }, Interlocked.Increment(ref nextWalletId))
        .Add(MatchKey, static (db, tx, id) => { tx.MatchRecord.Insert(new MatchRecord(id, 1)); return Result.Ok(); }, Interlocked.Increment(ref nextMatchId))
        .Commit());

    [Benchmark(OperationsPerInvoke = Transactions)]
    public Task LockedRootOnly() => Fan(Transactions, _ => LockedRootOnlyOnce());

    [Benchmark(OperationsPerInvoke = TwoPhaseTransactions)]
    public Task LockedRootAndChildTwoPhase() => Fan(TwoPhaseTransactions, _ => LockedRootAndChildOnce());

    private async Task<Result> LockedRootOnlyOnce() {
        await using var tx = (await bench.Ctx.LockMultiTx(static p => p.HostRootDb())).Unwrap();
        var first = await tx.Run(static (db, t, id) => { t.HostWallet.Insert(new HostWallet(id, 1)); return Result.Ok(); }, Interlocked.Increment(ref nextWalletId));
        if (first.IsError()) return first;
        var second = await tx.Run(static (db, t, id) => { t.HostWallet.Insert(new HostWallet(id, 2)); return Result.Ok(); }, Interlocked.Increment(ref nextWalletId));
        if (second.IsError()) return second;
        return await tx.Commit();
    }

    private async Task<Result> LockedRootAndChildOnce() {
        await using var tx = (await bench.Ctx.LockMultiTx(static p => p.HostRootDb().MatchDb(MatchKey))).Unwrap();
        var root = await tx.Run(static (db, t, id) => { t.HostWallet.Insert(new HostWallet(id, 1)); return Result.Ok(); }, Interlocked.Increment(ref nextWalletId));
        if (root.IsError()) return root;
        var child = await tx.Run(MatchKey, static (db, t, id) => { t.MatchRecord.Insert(new MatchRecord(id, 1)); return Result.Ok(); }, Interlocked.Increment(ref nextMatchId));
        if (child.IsError()) return child;
        return await tx.Commit();
    }

    static private async Task Fan(int count, Func<int, Task<Result>> start) {
        var tasks = new Task<Result>[count];
        for (var i = 0; i < count; i++) tasks[i] = start(i);
        foreach (var result in await Task.WhenAll(tasks)) result.ThrowIfError();
    }
}
