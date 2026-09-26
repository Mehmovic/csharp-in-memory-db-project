using System.Diagnostics;
using RhinoDB.Core;
using RhinoDB.Lib.Durability;
using RhinoDB.Lib.Tables;

namespace RhinoDB.Lib.Durability.Test;

// Diagnostic-style test, not a BenchmarkDotNet benchmark - WriteAheadLog is internal,
// deliberately not exposed to RhinoDB.Sandbox.Benchmark (it's meant to stay an
// implementation detail wired through ColdStore, not a public API surface - adding it as
// an InternalsVisibleTo friend broke the generator's protected-vs-protected-internal
// override accessibility for every generated {Db} class in that project). Validates the
// group-commit mechanism under SUSTAINED load: many successive bursts fired without
// individually awaiting each call before issuing the next, matching how the real single
// writer thread actually dispatches (PooledOperation.Run() never blocks on one operation's
// durability before dequeuing the next - see DbExecutionLoop.RunLoop). A first, wrong
// version of this test used 16 workers each awaiting their own append before issuing their
// next one - that self-throttling request-response pattern measured ~280us/op, ~10x worse,
// because it doesn't model anything the real system actually does: it starves the group
// of concurrent arrivals to piggyback on. This version's shape is what Phase 0's
// `WalPrototypeBenchmarks.BatchFreshKeyConfirmedInserts` already validated at one size;
// this test extends that to many successive bursts specifically to catch degradation over
// a longer run that a single burst wouldn't reveal. A full end-to-end sustained benchmark
// through the public DbContext API belongs in Step 7 once ColdStore wiring exists
// (Docs/05-wal-design.md); this is the narrower, WAL-only check.
public class WriteAheadLogSustainedLoadTests {
    private string dir = "";

    [SetUp]
    public void SetUp() {
        dir = Path.Combine(Path.GetTempPath(), "rhinodb-wal-sustained-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
    }

    [TearDown]
    public void TearDown() {
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Test]
    public async Task SustainedConfirmedAppends_OverManySuccessiveBursts_ThroughputStaysStable() {
        const int BatchSize = 5_000;
        const int BatchCount = 10;
        const int PayloadSize = 64;

        using var wal = WriteAheadLog.Create(Path.Combine(dir, "wal.dat"), Guid.NewGuid(), 0).Unwrap();
        var payload = new byte[PayloadSize];
        var lsn = 0L;
        var batchMicros = new double[BatchCount];

        // Untimed warm-up, same rationale as PersistentTableBenchmarks.cs's warm-up churn (which
        // uses a comparably large fixed row count for the same reason): JIT and first-write-to-a-
        // region costs would otherwise show up as false "degradation" in the first measured batch
        // rather than the steady state this test actually cares about.
        const int WarmupOperations = 20_000;
        var warmup = new Task<DbError?>[WarmupOperations];
        for (var i = 0; i < WarmupOperations; i++) {
            var warmupLsn = ++lsn;
            warmup[i] = wal.AppendConfirmed(warmupLsn, WalEntryKind.Operation, new WalChange[] { new(1, ChangeKind.Insert, BitConverter.GetBytes(warmupLsn), payload) });
        }
        await Task.WhenAll(warmup);

        for (var b = 0; b < BatchCount; b++) {
            var pending = new Task<DbError?>[BatchSize];
            var stopwatch = Stopwatch.StartNew();
            for (var i = 0; i < BatchSize; i++) {
                var thisLsn = ++lsn;
                var change = new WalChange(1, ChangeKind.Insert, BitConverter.GetBytes(thisLsn), payload);
                pending[i] = wal.AppendConfirmed(thisLsn, WalEntryKind.Operation, new WalChange[] { change });
            }
            await Task.WhenAll(pending);
            stopwatch.Stop();
            batchMicros[b] = stopwatch.Elapsed.TotalMicroseconds / BatchSize;
            foreach (var t in pending) Assert.That(t.Result, Is.Null);
        }

        var totalOps = BatchSize * BatchCount;
        TestContext.Out.WriteLine($"{BatchCount} successive bursts of {BatchSize} Confirmed appends ({totalOps} total, " +
            "fired without individually awaiting each one - matching how the real single writer thread dispatches, " +
            "not a self-throttling request-response loop): " +
            string.Join(", ", batchMicros.Select((us, i) => $"batch {i}: {us:F2} us/op")));

        var firstHalfMedian = Median(batchMicros.Take(BatchCount / 2));
        var secondHalfMedian = Median(batchMicros.Skip(BatchCount / 2));
        TestContext.Out.WriteLine($"First-half median: {firstHalfMedian:F2} us/op, second-half median: {secondHalfMedian:F2} us/op.");

        // Generous sanity bounds, not tight perf claims: the settled libmdbx per-commit cost this
        // WAL replaces measured ~50-130us (Docs/03-roadmap.md). Two properties this test exists to
        // catch a regression in: sustained per-op cost staying meaningfully under that ceiling
        // (not just Phase 0's single-burst numbers), and no degradation across a longer run (a
        // resource leak or unbounded growth in the group-commit path would show up as the second
        // half getting markedly slower than the first).
        //
        // The absolute bound checks the MINIMUM per-batch cost, not an average: a single clean
        // batch under the bound proves the mechanism's actual steady-state capability, and a gross
        // regression (e.g. back to the ~280us self-throttling pattern) fails the min too, so the
        // check keeps its teeth regardless of noise.
        //
        // The degradation check compares MEDIANS, not averages (confirmed necessary, not a
        // hypothetical): Windows Defender realtime scanning of the temp-dir file has already
        // produced misleading I/O numbers in this repo before (commit be219e6), and this test
        // failed again the same way while implementing an unrelated change - one batch spiked to
        // ~30x its neighbors (a single external-interference event: a real resource leak would
        // show costs climbing across ALL batches, not one isolated spike) while the other 9
        // batches, and the absolute-minimum check, stayed clean. A straight average over only 5
        // batches per half is not resilient to one such outlier - it swamps the other four. The
        // median is: it takes multiple slow batches (i.e. genuine sustained degradation, not one
        // noisy sample) to move it.
        Assert.That(secondHalfMedian, Is.LessThan(firstHalfMedian * 3),
            "Per-op cost degraded significantly over a sustained run - possible resource leak or unbounded growth in the group-commit path.");
        Assert.That(batchMicros.Skip(1).Min(), Is.LessThan(50.0),
            "Sustained per-op Confirmed cost regressed well above the settled libmdbx commit cost it was built to beat.");
    }

    static private double Median(IEnumerable<double> values) {
        var sorted = values.OrderBy(v => v).ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2.0 : sorted[mid];
    }
}
