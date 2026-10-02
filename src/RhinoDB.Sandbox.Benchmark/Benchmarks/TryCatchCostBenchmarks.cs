using BenchmarkDotNet.Attributes;
using RhinoDB.Core;
using RhinoDB.Lib.Execution;
using RhinoDB.Sandbox.Benchmark.Schema;

namespace RhinoDB.Sandbox.Benchmark.Benchmarks;

// Measures the try/catch PooledOperation wraps around the user operation delegate:
//   try { result = operation!.Invoke(ctx, tx, args); }
//   catch (Exception ex) { result = TValue.FromException(ex); }
//
// The question is whether that guard costs anything on the happy path. Two layers,
// because they answer different questions:
//
//  1. Guard cost in isolation. Two Benchmark methods with identical work, one inside a
//     try/catch and one not. The try lives in the *benchmark* method rather than in a
//     helper, because a method containing an EH region is compiled into funclets -
//     which is exactly what we want to measure. If they land within noise, the happy
//     path is free and the only cost is paid when an exception is actually thrown.
//
//  2. The same comparison through the real engine: an operation that reports failure
//     via Result.Error versus one that throws. This is the number that actually
//     matters, because it is what a business rule failing costs through db.Run, and it
//     lands on the writer thread.
//
// Expected shape: Direct and Guarded indistinguishable, while the throwing arms land
// orders of magnitude higher - the two-phase unwind plus the exception allocation.
[MemoryDiagnoser]
public class TryCatchCostBenchmarks {
    private InstantBenchDb db = null!;

    [GlobalSetup]
    public void Setup() => db = new InstantBenchDb();

    // ---- Layer 1: the guard itself ----

    [Benchmark(Baseline = true)]
    public Result Direct_NoGuard() => Result.Ok();

    [Benchmark]
    public Result Guarded_TryCatch() {
        try { return Result.Ok(); }
        catch (Exception ex) { return Result.Error(DbError.IndexKeyNotFound(ex)); }
    }

    // Magnitude of the throw path, with nothing else in the frame so the unwind cost
    // is visible rather than hidden behind real work.
    [Benchmark]
    public Result Guarded_ActuallyThrows() {
        try { throw new InvalidOperationException("benchmark"); }
        catch (Exception ex) { return Result.Error(DbError.IndexKeyNotFound(ex)); }
    }

    // ---- Layer 2: through the engine ----

    [Benchmark]
    public ValueTask<Result> Run_ReturnsResultError() =>
        db.Run(static (ctx, tx) => Result.Error(DbError.IndexKeyNotFound()), PropagationMode.Optimistic);

    [Benchmark]
    public ValueTask<Result> Run_DelegateThrows() =>
        db.Run(static (ctx, tx) => throw new InvalidOperationException("benchmark"), PropagationMode.Optimistic);
}