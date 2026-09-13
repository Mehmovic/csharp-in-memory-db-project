using BenchmarkDotNet.Attributes;
using RhinoDB.Core;
using RhinoDB.Lib.Execution;

namespace RhinoDB.Run.Server.Benchmark.Benchmarks;

[MemoryDiagnoser]
public class SubmissionOverheadBenchmarks {
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
}
