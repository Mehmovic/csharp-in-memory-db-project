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
        dataDir = Path.Combine(Path.GetTempPath(), $"rhinodb-bench-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        cold = ColdStore.Open(dataDir, MapSizeUpperBytes).Unwrap();
        db = new PersistentBenchDb(cold);
        lookupKey = RecordCount / 2;
        nextInsertId = RecordCount;

        var seeded = 0;
        while (seeded < RecordCount) {
            var start = seeded;
            var end = Math.Min(seeded + SeedBatchSize, RecordCount);
            db.Run((ctx, tx) => {
                for (var i = start; i < end; i++) tx.PersistentWidgets.Insert(new PersistentWidget(i, i));
                return Result.Ok();
            }, PropagationMode.Optimistic).GetAwaiter().GetResult();
            seeded = end;
        }
    }

    [GlobalCleanup]
    public void Cleanup() {
        cold.Dispose();
        Directory.Delete(dataDir, recursive: true);
    }

    [Benchmark]
    public Task<Result<PersistentWidget>> Get() =>
        db.Run<PersistentWidget>((ctx, tx) => tx.PersistentWidgets.Get(lookupKey), PropagationMode.Optimistic);

    [Benchmark]
    public Task<Result> InsertOptimistic() {
        var id = ++nextInsertId;
        return db.Run((ctx, tx) => { tx.PersistentWidgets.Insert(new PersistentWidget(id, id)); return Result.Ok(); }, PropagationMode.Optimistic);
    }

    [Benchmark]
    public Task<Result> InsertConfirmed() {
        var id = ++nextInsertId;
        return db.Run((ctx, tx) => { tx.PersistentWidgets.Insert(new PersistentWidget(id, id)); return Result.Ok(); }, PropagationMode.Confirmed);
    }

    [Benchmark]
    public Task<Result> Update() =>
        db.Run((ctx, tx) => { tx.PersistentWidgets.Update(lookupKey, new PersistentWidget(lookupKey, ++nextInsertId)); return Result.Ok(); }, PropagationMode.Optimistic);
}
