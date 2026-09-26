using BenchmarkDotNet.Attributes;
using RhinoDB.Core;
using RhinoDB.Lib.Cold;
using RhinoDB.Lib.Execution;
using RhinoDB.Sandbox.Benchmark.Schema;

namespace RhinoDB.Sandbox.Benchmark.Benchmarks;

[MemoryDiagnoser]
public class ThroughputBenchmarks {
    private const int ConcurrentOperations = 10_000;
    private const int SeedRecordCount = 10_000;
    private const int WarmupRows = 20_000;
    private const int SeedBatchSize = 100_000;
    static private readonly nint MapSizeUpperBytes = unchecked((nint)137_438_953_472L);

    private InstantBenchDb instantDb = null!;
    private int instantLookupKey;
    private int instantNextInsertId;
    private int instantNextValue;

    private string persistentDataDir = null!;
    private ColdStore persistentCold = null!;
    private PersistentBenchDb persistentDb = null!;
    private int persistentLookupKey;
    private int persistentNextInsertId;
    private int persistentNextValue;

    [GlobalSetup]
    public void Setup() {
        instantDb = new InstantBenchDb();
        instantLookupKey = SeedRecordCount / 2;
        instantNextInsertId = SeedRecordCount;
        instantNextValue = 0;
        var instantSeeded = 0;
        while (instantSeeded < SeedRecordCount) {
            var start = instantSeeded;
            var end = Math.Min(instantSeeded + SeedBatchSize, SeedRecordCount);
            instantDb.Run((ctx, tx) => {
                for (var i = start; i < end; i++) tx.InstantWidget.Insert(new InstantWidget(i, i));
                return Result.Ok();
            }, PropagationMode.Optimistic).AsTask().GetAwaiter().GetResult();
            instantSeeded = end;
        }

        persistentDataDir = Path.Combine(Path.GetTempPath(), "rhinodb-bench", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(persistentDataDir);
        var estimatedBytes = Math.Max(16L * 1024 * 1024, SeedRecordCount * 64L) + 64L * 1024 * 1024;
        var sizeNowBytes = unchecked((nint)Math.Min(estimatedBytes, (long)MapSizeUpperBytes));
        persistentCold = ColdStore.Open(persistentDataDir, MapSizeUpperBytes, sizeNowBytes).Unwrap();
        persistentDb = new PersistentBenchDb(persistentCold);
        persistentLookupKey = SeedRecordCount / 2;
        persistentNextInsertId = SeedRecordCount;
        persistentNextValue = 0;

        for (var i = 0; i < WarmupRows; i += SeedBatchSize) {
            var start = -1 - i;
            var end = -1 - Math.Min(i + SeedBatchSize, WarmupRows);
            persistentDb.Run((ctx, tx) => {
                for (var k = start; k > end; k--) tx.PersistentWidget.Insert(new PersistentWidget(k, k));
                return Result.Ok();
            }, PropagationMode.Confirmed).AsTask().GetAwaiter().GetResult();
        }

        var persistentSeeded = 0;
        while (persistentSeeded < SeedRecordCount) {
            var start = persistentSeeded;
            var end = Math.Min(persistentSeeded + SeedBatchSize, SeedRecordCount);
            persistentDb.Run((ctx, tx) => {
                for (var i = start; i < end; i++) tx.PersistentWidget.Insert(new PersistentWidget(i, i));
                return Result.Ok();
            }, PropagationMode.Optimistic).AsTask().GetAwaiter().GetResult();
            persistentSeeded = end;
        }
    }

    [GlobalCleanup]
    public void Cleanup() {
        persistentCold.Dispose();
        Directory.Delete(persistentDataDir, recursive: true);
    }

    [Benchmark]
    public Task InstantConcurrentInserts() {
        var tasks = new Task[ConcurrentOperations];
        for (var i = 0; i < ConcurrentOperations; i++) {
            var id = Interlocked.Increment(ref instantNextInsertId);
            tasks[i] = instantDb.Run((ctx, tx) => { tx.InstantWidget.Insert(new InstantWidget(id, id)); return Result.Ok(); }, PropagationMode.Optimistic).AsTask();
        }
        return Task.WhenAll(tasks);
    }

    [Benchmark]
    public Task InstantConcurrentUpdates() {
        var tasks = new Task[ConcurrentOperations];
        for (var i = 0; i < ConcurrentOperations; i++) {
            var value = Interlocked.Increment(ref instantNextValue);
            tasks[i] = instantDb.Run((ctx, tx) => { tx.InstantWidget.Update(instantLookupKey, new InstantWidget(instantLookupKey, value)); return Result.Ok(); }, PropagationMode.Optimistic).AsTask();
        }
        return Task.WhenAll(tasks);
    }

    [Benchmark]
    public Task PersistentOptimisticConcurrentInserts() {
        var tasks = new Task[ConcurrentOperations];
        for (var i = 0; i < ConcurrentOperations; i++) {
            var id = Interlocked.Increment(ref persistentNextInsertId);
            tasks[i] = persistentDb.Run((ctx, tx) => { tx.PersistentWidget.Insert(new PersistentWidget(id, id)); return Result.Ok(); }, PropagationMode.Optimistic).AsTask();
        }
        return Task.WhenAll(tasks);
    }

    [Benchmark]
    public Task PersistentOptimisticConcurrentUpdates() {
        var tasks = new Task[ConcurrentOperations];
        for (var i = 0; i < ConcurrentOperations; i++) {
            var value = Interlocked.Increment(ref persistentNextValue);
            tasks[i] = persistentDb.Run((ctx, tx) => { tx.PersistentWidget.Update(persistentLookupKey, new PersistentWidget(persistentLookupKey, value)); return Result.Ok(); }, PropagationMode.Optimistic).AsTask();
        }
        return Task.WhenAll(tasks);
    }
}
