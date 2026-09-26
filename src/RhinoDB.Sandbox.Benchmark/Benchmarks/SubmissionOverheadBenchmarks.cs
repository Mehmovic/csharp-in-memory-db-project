using BenchmarkDotNet.Attributes;
using RhinoDB.Core;
using RhinoDB.Lib.Execution;
using RhinoDB.Sandbox.Benchmark.Schema;

namespace RhinoDB.Sandbox.Benchmark.Benchmarks;

[MemoryDiagnoser]
public class SubmissionOverheadBenchmarks {
    private InstantBenchDb db = null!;

    [GlobalSetup]
    public void Setup() => db = new InstantBenchDb();

    [Benchmark]
    public Task<Result> BareTaskCompletionSource() {
        var tcs = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult(Result.Ok());
        return tcs.Task;
    }

    [Benchmark]
    public Action BareClosureOverEnumAndInt() {
        var mode = PropagationMode.Optimistic;
        var value = 42;
        return () => { _ = mode; _ = value; };
    }

    [Benchmark]
    public ValueTask<Result> PooledRunNoOp() =>
        db.Run(static (ctx, tx) => Result.Ok(), PropagationMode.Optimistic);
}
