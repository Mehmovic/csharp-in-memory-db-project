using BenchmarkDotNet.Attributes;
using RhinoDB.Core;
using RhinoDB.Lib.Execution;
using RhinoDB.Sandbox.Benchmark.Schema;

namespace RhinoDB.Sandbox.Benchmark.Benchmarks;

[MemoryDiagnoser]
[InvocationCount(1)]
public class ChildColdActivationBenchmarks {
    private const int MatchKey = 1;
    private const int SeedBatch = 10_000;

    [Params(0, 1_000, 100_000)]
    public int Rows;

    private string directory = null!;
    private BenchHost bench = null!;

    [GlobalSetup]
    public void Setup() {
        directory = BenchHost.NewDirectory();
        var seed = BenchHost.Start(directory);
        for (var start = 0; start < Rows; start += SeedBatch) {
            var range = (Start: start, End: Math.Min(start + SeedBatch, Rows));
            seed.Ctx.BeginTx(MatchKey, (db, tx) => {
                for (var i = range.Start; i < range.End; i++) tx.MatchRecord.Insert(new MatchRecord(i, i));
                return Result.Ok();
            }).GetAwaiter().GetResult().ThrowIfError();
        }
        seed.Stop();
    }

    [IterationSetup]
    public void StartHost() => bench = BenchHost.Start(directory);

    [Benchmark]
    public MatchDb ActivateExistingChild() => bench.Match(MatchKey);

    [IterationCleanup]
    public void StopHost() => bench.Stop();

    [GlobalCleanup]
    public void Cleanup() {
        try { Directory.Delete(directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

[MemoryDiagnoser]
public class ChildLifecycleBenchmarks {
    private const int ActiveKey = 1;

    private BenchHost bench = null!;
    private int nextKey = 1_000;

    [GlobalSetup]
    public void Setup() {
        bench = BenchHost.Start(BenchHost.NewDirectory());
        bench.Match(ActiveKey);
    }

    [GlobalCleanup]
    public void Cleanup() => bench.StopAndDelete();

    [Benchmark]
    public Task<Result<MatchDb>> LookupActiveChild() =>
        bench.Host.GetOrActivateChildAsync<MatchDb, MatchDbTransaction, int>(ActiveKey);

    [Benchmark]
    public Task<Result> BeginTxOnActiveChild() => bench.Ctx.BeginTx(ActiveKey, static (db, tx) => Result.Ok());

    [Benchmark]
    public async Task CreateUseAndDisposeChild() {
        var key = Interlocked.Increment(ref nextKey);
        (await bench.Ctx.BeginTx(key, (db, tx) => { tx.MatchRecord.Insert(new MatchRecord(key, 1)); return Result.Ok(); })).ThrowIfError();
        (await bench.Host.DisposeChildAsync<MatchDb, MatchDbTransaction, int>(key)).ThrowIfError();
    }
}
