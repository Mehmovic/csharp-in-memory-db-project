using BenchmarkDotNet.Attributes;
using RhinoDB.Core;
using RhinoDB.Lib.Cold;
using RhinoDB.Lib.Execution;
using RhinoDB.Run.Server.Benchmark.Schema;

namespace RhinoDB.Run.Server.Benchmark.Benchmarks;

[MemoryDiagnoser]
public class ConfirmedCoalescingBenchmarks {
    private const int SeedRecordCount = 10_000;
    private const int WarmupRows = 20_000;
    private const int SeedBatchSize = 100_000;
    private const int ConcurrentWriters = 16;
    static private readonly nint MapSizeUpperBytes = unchecked((nint)137_438_953_472L);

    [Params(0, 20)]
    public int CommitCoalescingWindowMs;

    private string dataDir = null!;
    private ColdStore cold = null!;
    private PersistentBenchDb db = null!;
    private int nextInsertId;
    private int lookupKey;
    private int nextValue;

    [GlobalSetup]
    public void Setup() {
        dataDir = Path.Combine(Path.GetTempPath(), "rhinodb-bench", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        var estimatedBytes = Math.Max(16L * 1024 * 1024, SeedRecordCount * 64L) + 64L * 1024 * 1024;
        var sizeNowBytes = unchecked((nint)Math.Min(estimatedBytes, (long)MapSizeUpperBytes));
        cold = ColdStore.Open(dataDir, MapSizeUpperBytes, sizeNowBytes, TimeSpan.FromMilliseconds(CommitCoalescingWindowMs)).Unwrap();
        db = new PersistentBenchDb(cold);
        nextInsertId = SeedRecordCount;
        lookupKey = SeedRecordCount / 2;
        nextValue = 0;

        for (var i = 0; i < WarmupRows; i += SeedBatchSize) {
            var start = -1 - i;
            var end = -1 - Math.Min(i + SeedBatchSize, WarmupRows);
            db.Run((ctx, tx) => {
                for (var k = start; k > end; k--) tx.PersistentWidget.Insert(new PersistentWidget(k, k));
                return Result.Ok();
            }, PropagationMode.Confirmed).AsTask().GetAwaiter().GetResult();
        }

        var seeded = 0;
        while (seeded < SeedRecordCount) {
            var start = seeded;
            var end = Math.Min(seeded + SeedBatchSize, SeedRecordCount);
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
    public Task ConcurrentConfirmedInserts() {
        var tasks = new Task[ConcurrentWriters];
        for (var i = 0; i < ConcurrentWriters; i++) {
            var id = Interlocked.Increment(ref nextInsertId);
            tasks[i] = db.Run((ctx, tx) => { tx.PersistentWidget.Insert(new PersistentWidget(id, id)); return Result.Ok(); }, PropagationMode.Confirmed).AsTask();
        }
        return Task.WhenAll(tasks);
    }

    [Benchmark]
    public Task ConcurrentConfirmedUpdates() {
        var tasks = new Task[ConcurrentWriters];
        for (var i = 0; i < ConcurrentWriters; i++) {
            var value = Interlocked.Increment(ref nextValue);
            tasks[i] = db.Run((ctx, tx) => { tx.PersistentWidget.Update(lookupKey, new PersistentWidget(lookupKey, value)); return Result.Ok(); }, PropagationMode.Confirmed).AsTask();
        }
        return Task.WhenAll(tasks);
    }
}
