using BenchmarkDotNet.Attributes;
using RhinoDB.Core;
using RhinoDB.Lib.Cold;
using RhinoDB.Lib.Execution;
using RhinoDB.Run.Server.Benchmark.Schema;

namespace RhinoDB.Run.Server.Benchmark.Benchmarks;

[MemoryDiagnoser]
public class PersistentTableBenchmarks {
    private const int SeedBatchSize = 100_000;
    static private readonly nint MapSizeUpperBytes = unchecked((nint)137_438_953_472L);

    [ParamsSource(nameof(RecordCounts))]
    public int RecordCount;

    static public IEnumerable<int> RecordCounts => BenchmarkScale.RecordCounts();

    private string dataDir = null!;
    private ColdStore cold = null!;
    private PersistentBenchDb db = null!;
    private int lookupKey;
    private int nextInsertId;

    [GlobalSetup]
    public void Setup() {
        dataDir = Path.Combine(Path.GetTempPath(), "rhinodb-bench", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        var estimatedBytes = Math.Max(16L * 1024 * 1024, RecordCount * 64L) + 64L * 1024 * 1024;
        var sizeNowBytes = unchecked((nint)Math.Min(estimatedBytes, (long)MapSizeUpperBytes));
        cold = ColdStore.Open(dataDir, MapSizeUpperBytes, sizeNowBytes).Unwrap();
        db = new PersistentBenchDb(cold);
        lookupKey = RecordCount / 2;
        nextInsertId = RecordCount;

        // Fixed warm-up churn, independent of RecordCount: a freshly-created file's
        // first-ever-written pages measure far slower than already-touched ones on this
        // machine (Windows Defender real-time scanning is the leading suspect - see
        // Docs/03-roadmap.md's 2026-09-13 entry). A real long-running database's file is
        // never actually cold either, so this makes the benchmark measure steady-state
        // write cost instead of a one-time-per-run environmental artifact.
        const int WarmupRows = 20_000;
        for (var i = 0; i < WarmupRows; i += SeedBatchSize) {
            var start = -1 - i;
            var end = -1 - Math.Min(i + SeedBatchSize, WarmupRows);
            db.Run((ctx, tx) => {
                for (var k = start; k > end; k--) tx.PersistentWidget.Insert(new PersistentWidget(k, k));
                return Result.Ok();
            }, PropagationMode.Confirmed).AsTask().GetAwaiter().GetResult();
        }

        var seeded = 0;
        while (seeded < RecordCount) {
            var start = seeded;
            var end = Math.Min(seeded + SeedBatchSize, RecordCount);
            db.Run((ctx, tx) => {
                for (var i = start; i < end; i++) tx.PersistentWidget.Insert(new PersistentWidget(i, i));
                return Result.Ok();
            }, PropagationMode.Optimistic).AsTask().GetAwaiter().GetResult();
            seeded = end;
        }
    }

    [GlobalCleanup]
    public void Cleanup() {
        cold.Dispose();
        Directory.Delete(dataDir, recursive: true);
    }

    [Benchmark]
    public ValueTask<Result<PersistentWidget>> Get() =>
        db.Run<PersistentWidget>((ctx, tx) => tx.PersistentWidget.Find(lookupKey).Get(), PropagationMode.Optimistic);

    [Benchmark]
    public ValueTask<Result> InsertOptimistic() {
        var id = ++nextInsertId;
        return db.Run((ctx, tx) => { tx.PersistentWidget.Insert(new PersistentWidget(id, id)); return Result.Ok(); }, PropagationMode.Optimistic);
    }

    [Benchmark]
    public ValueTask<Result> InsertConfirmed() {
        var id = ++nextInsertId;
        return db.Run((ctx, tx) => { tx.PersistentWidget.Insert(new PersistentWidget(id, id)); return Result.Ok(); }, PropagationMode.Confirmed);
    }

    [Benchmark]
    public ValueTask<Result> Update() =>
        db.Run((ctx, tx) => { tx.PersistentWidget.Update(lookupKey, new PersistentWidget(lookupKey, ++nextInsertId)); return Result.Ok(); }, PropagationMode.Optimistic);
}
