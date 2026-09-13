using BenchmarkDotNet.Attributes;
using RhinoDB.Core;
using RhinoDB.Lib.Execution;
using RhinoDB.Run.Server.Benchmark.Schema;

namespace RhinoDB.Run.Server.Benchmark.Benchmarks;

[MemoryDiagnoser]
public class InstantTableBenchmarks {
    private const int SeedBatchSize = 100_000;

    [ParamsSource(nameof(RecordCounts))]
    public int RecordCount;

    static public IEnumerable<int> RecordCounts => BenchmarkScale.RecordCounts();

    private InstantBenchDb db = null!;
    private int lookupKey;
    private int nextInsertId;

    [GlobalSetup]
    public void Setup() {
        db = new InstantBenchDb();
        lookupKey = RecordCount / 2;
        nextInsertId = RecordCount;

        var seeded = 0;
        while (seeded < RecordCount) {
            var start = seeded;
            var end = Math.Min(seeded + SeedBatchSize, RecordCount);
            db.Run((ctx, tx) => {
                for (var i = start; i < end; i++) tx.InstantWidget.Insert(new InstantWidget(i, i));
                return Result.Ok();
            }, PropagationMode.Optimistic).GetAwaiter().GetResult();
            seeded = end;
        }
    }

    [Benchmark]
    public Task<Result<InstantWidget>> Get() =>
        db.Run<InstantWidget>((ctx, tx) => tx.InstantWidget.Get(lookupKey), PropagationMode.Optimistic);

    [Benchmark]
    public Task<Result> Insert() {
        var id = ++nextInsertId;
        return db.Run((ctx, tx) => { tx.InstantWidget.Insert(new InstantWidget(id, id)); return Result.Ok(); }, PropagationMode.Optimistic);
    }

    [Benchmark]
    public Task<Result> Update() =>
        db.Run((ctx, tx) => { tx.InstantWidget.Update(lookupKey, new InstantWidget(lookupKey, ++nextInsertId)); return Result.Ok(); }, PropagationMode.Optimistic);
}
