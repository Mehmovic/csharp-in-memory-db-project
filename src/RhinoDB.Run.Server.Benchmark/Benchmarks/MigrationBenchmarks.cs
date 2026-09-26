using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using RhinoDB.Core;
using RhinoDB.Lib.Cold;
using RhinoDB.Lib.Execution;
using RhinoDB.Run.Server.Benchmark.Schema;

namespace RhinoDB.Run.Server.Benchmark.Benchmarks;

// Phase 4 step 20's required perf gate (Docs/06-schema-migration.md §9): measure the real drop-recreate-copy
// migration transaction's wall-clock cost at 100k/1M rows before calling the mechanics done, grounding the
// design doc's "fine at league scale, potentially minutes at 50M-row scale" estimate in a real number
// instead of a guess. Deliberately NOT the default BenchmarkDotNet job - a migration is a rare, expensive,
// once-per-schema-change operation, not a hot repeated call, so a handful of iterations is enough for a
// representative average without the default job's much larger iteration count turning a 1M-row run into
// many extra minutes for no additional signal.
[SimpleJob(RunStrategy.Monitoring, launchCount: 1, warmupCount: 0, iterationCount: 3)]
public class MigrationBenchmarks {
    private const int SeedBatchSize = 100_000;
    static private readonly nint MapSizeUpperBytes = unchecked((nint)137_438_953_472L);

    [Params(100_000, 1_000_000)]
    public int RecordCount;

    private string dataDir = null!;
    private ColdStore cold = null!;
    private PersistentBenchDb db = null!;

    [GlobalSetup]
    public void Setup() {
        dataDir = Path.Combine(Path.GetTempPath(), "rhinodb-migration-bench", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        var estimatedBytes = Math.Max(16L * 1024 * 1024, RecordCount * 64L) + 64L * 1024 * 1024;
        var sizeNowBytes = unchecked((nint)Math.Min(estimatedBytes, (long)MapSizeUpperBytes));

        // RunMigration() reads via a raw mdbx cursor over the table's own dbi - it never sees a same-session
        // insert's in-memory-only state (there's no automatic live checkpointing), so the seeded rows must
        // actually be recovered into mdbx first, via a real close + reopen + CompleteRecovery, exactly like
        // a genuine prior session's committed data would be found on the next process start.
        using (var seedCold = ColdStore.Open(dataDir, MapSizeUpperBytes, sizeNowBytes).Unwrap()) {
            var seedDb = new PersistentBenchDb(seedCold);
            var seeded = 0;
            while (seeded < RecordCount) {
                var start = seeded;
                var end = Math.Min(seeded + SeedBatchSize, RecordCount);
                seedDb.Run((ctx, tx) => {
                    for (var i = start; i < end; i++) tx.PersistentWidget.Insert(new PersistentWidget(i, i));
                    return Result.Ok();
                }, PropagationMode.Confirmed).AsTask().GetAwaiter().GetResult();
                seeded = end;
            }
        }

        cold = ColdStore.Open(dataDir, MapSizeUpperBytes, sizeNowBytes).Unwrap();
        db = new PersistentBenchDb(cold);
        cold.CompleteRecovery();
    }

    [GlobalCleanup]
    public void Cleanup() {
        cold.Dispose();
        Directory.Delete(dataDir, recursive: true);
    }

    [Benchmark]
    public Result RunMigration() => db.RunMigration();
}
