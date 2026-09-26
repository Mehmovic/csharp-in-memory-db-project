using BenchmarkDotNet.Attributes;
using RhinoDB.Core;
using RhinoDB.Lib.Execution;
using RhinoDB.Sandbox.Benchmark.Schema;

namespace RhinoDB.Sandbox.Benchmark.Benchmarks;

// Attributes the ~3.4 us / 32 B per generated read that RowAccessBenchmarks measured.
//
// The read itself is not the suspect: a DenseArray row access is ~1.5 ns with zero
// allocation (StorageGet_* in RowAccessBenchmarks). So this suite splits the path into
// its three layers and measures each in isolation:
//
//  1. submission only      - Run(...) with an empty body, no query at all
//  2. submission + query   - the generated primary-key read
//  3. await vs no-await    - each operation awaited individually, vs a batch submitted
//                            without awaiting and only awaited at the end. The batch
//                            variant isolates per-operation submission cost from the
//                            per-await continuation machinery (the execution loop sets
//                            RunContinuationsAsynchronously, so a waiting caller is
//                            resumed through a thread-pool dispatch).
//
// If Enqueue_NoOp lands at the same time/allocation as Enqueue_Find, the query is free
// and everything belongs to the actor-model round trip - which decides where any fix
// belongs.
//
// Outcome (2026-09-23): query confirmed free; the 32 B + ~850 ns is the
// RCA=true continuation dispatch (RCA=false measured 0 B / 2.64 us but runs
// caller continuations on the engine thread). Decision: keep RCA=true for
// actor isolation; hot paths batch submits (~185 ns/op). See
// Docs/01-performance-principles.md section 3.
[MemoryDiagnoser]
public class ReadPathBenchmarks {
    private const int SeedBatchSize = 100_000;
    private const int BatchSize = 100;

    [Params(10_000)]
    public int RecordCount;

    private InstantBenchDb db = null!;
    private int lookupKey;
    private ValueTask<Result>[] pendingResults = null!;
    private ValueTask<Result<InstantWidget>>[] pendingReads = null!;

    [GlobalSetup]
    public void Setup() {
        db = new InstantBenchDb();
        lookupKey = RecordCount / 2;
        pendingResults = new ValueTask<Result>[BatchSize];
        pendingReads = new ValueTask<Result<InstantWidget>>[BatchSize];

        var seeded = 0;
        while (seeded < RecordCount) {
            var start = seeded;
            var end = Math.Min(seeded + SeedBatchSize, RecordCount);
            db.Run((ctx, tx) => {
                for (var i = start; i < end; i++) tx.InstantWidget.Insert(new InstantWidget(i, i));
                return Result.Ok();
            }, PropagationMode.Optimistic).AsTask().GetAwaiter().GetResult();
            seeded = end;
        }
    }

    // ---- Layer 1: submission only (no query) ----

    [Benchmark]
    public ValueTask<Result> Enqueue_NoOp() =>
        db.Run(static (ctx, tx) => Result.Ok(), PropagationMode.Optimistic);

    [Benchmark(OperationsPerInvoke = BatchSize)]
    public async Task SubmitBatch_NoAwait_NoOp() {
        for (var i = 0; i < BatchSize; i++)
            pendingResults[i] = db.Run(static (ctx, tx) => Result.Ok(), PropagationMode.Optimistic);

        for (var i = 0; i < BatchSize; i++) await pendingResults[i];
    }

    // ---- Layer 2: submission + the generated read ----

    [Benchmark]
    public ValueTask<Result<InstantWidget>> Enqueue_Find() =>
        db.Run<InstantWidget, int>(static (ctx, tx, key) => tx.InstantWidget.Primary.Find(key).Get(), lookupKey, PropagationMode.Optimistic);

    [Benchmark(OperationsPerInvoke = BatchSize)]
    public async Task SubmitBatch_NoAwait_Find() {
        for (var i = 0; i < BatchSize; i++)
            pendingReads[i] = db.Run<InstantWidget, int>(static (ctx, tx, key) => tx.InstantWidget.Primary.Find(key).Get(), lookupKey, PropagationMode.Optimistic);

        for (var i = 0; i < BatchSize; i++) await pendingReads[i];
    }

    // ---- Layer 3: same batch, but awaited one at a time ----

    [Benchmark(OperationsPerInvoke = BatchSize)]
    public async Task SubmitBatch_AwaitEach_Find() {
        for (var i = 0; i < BatchSize; i++)
            await db.Run<InstantWidget, int>(static (ctx, tx, key) => tx.InstantWidget.Primary.Find(key).Get(), lookupKey, PropagationMode.Optimistic);
    }
}
