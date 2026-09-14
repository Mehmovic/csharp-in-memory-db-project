using RhinoDB.Lib.Durability;
using RhinoDB.Native;

namespace RhinoDB.Lib.Durability.Test;

// EvictionBatch/EvictionBatchApplier are the batched write-through eviction rule,
// Docs/05-wal-design.md Phase 3 - real libmdbx, no mocking layer, matching
// CheckpointEngineTests.cs's convention (they share the same one-mdbx-txn/one-commit/
// one-fsync shape, just without a WAL/watermark).
public class EvictionBatchTests {
    private const uint CreateDbi = 0x40000;
    private const ushort DefaultUnixMode = 0b110_100_100;
    private const uint SafeNoSync = 0x10000;
    private const uint TableId = 1;

    private string dir = "";
    private MdbxEnvironment env = null!;
    private uint widgetsDbi;

    [SetUp]
    public void SetUp() {
        dir = Path.Combine(Path.GetTempPath(), "rhinodb-eviction-batch-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        MdbxEnvironment.Create(out var created);
        env = created!;
        env.SetMaxDbs(16);
        env.SetGeometry(-1, -1, -1, -1, -1, -1);
        env.Open(dir, SafeNoSync, DefaultUnixMode);

        env.BeginTxn(0, out var txn);
        txn!.OpenDbi("widgets", CreateDbi, out widgetsDbi);
        txn.Commit();
    }

    [TearDown]
    public void TearDown() {
        env.Dispose();
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private byte[]? ReadRaw(byte[] key) {
        env.BeginTxn(0, out var txn);
        using var tx = txn!;
        var rc = tx.Get(widgetsDbi, key, out var value);
        return rc == 0 ? value : null;
    }

    [Test]
    public void Stage_UntilTheSizeThresholdIsCrossed_SignalsTheCallerToFlush() {
        var batch = new EvictionBatch(sizeThresholdBytes: 4);

        var signaledEarly = batch.Stage(TableId, [1, 2]);
        var signaledAtThreshold = batch.Stage(TableId, [3, 4]);

        Assert.That(signaledEarly, Is.False);
        Assert.That(signaledAtThreshold, Is.True, "Staging must signal once the size threshold is actually crossed, as a hard cap, not a soft hint.");
    }

    [Test]
    public void DrainStaged_ReturnsEverythingStagedAndResetsForTheNextBatch() {
        var batch = new EvictionBatch();
        batch.Stage(TableId, [1]);
        batch.Stage(TableId, [2]);

        var drained = batch.DrainStaged();
        var drainedAgain = batch.DrainStaged();

        Assert.That(drained, Has.Count.EqualTo(2));
        Assert.That(drainedAgain, Is.Empty, "A second drain with nothing newly staged must come back empty, not repeat the prior batch.");
    }

    [Test]
    public void Apply_WritesTheCurrentValueForEachCandidateInOneCommit() {
        var tableDbis = new Dictionary<uint, uint> { [TableId] = widgetsDbi };
        var candidates = new[] { new EvictionCandidate(TableId, [1]), new EvictionCandidate(TableId, [2]) };

        var result = EvictionBatchApplier.Apply(env, tableDbis, candidates, (_, key) => [key[0], 99]);

        Assert.That(result.IsOk(), Is.True);
        Assert.That(result.Unwrap(), Has.Count.EqualTo(2));
        Assert.That(ReadRaw([1]), Is.EqualTo(new byte[] { 1, 99 }));
        Assert.That(ReadRaw([2]), Is.EqualTo(new byte[] { 2, 99 }));
    }

    [Test]
    public void Apply_ARowNoLongerResidentAtApplyTime_IsSkippedNotWrittenAsStale() {
        var tableDbis = new Dictionary<uint, uint> { [TableId] = widgetsDbi };
        var candidates = new[] { new EvictionCandidate(TableId, [1]) };

        var result = EvictionBatchApplier.Apply(env, tableDbis, candidates, (_, _) => null);

        Assert.That(result.IsOk(), Is.True);
        Assert.That(result.Unwrap(), Is.Empty, "A candidate whose row vanished between staging and apply time must not be written through.");
        Assert.That(ReadRaw([1]), Is.Null);
    }

    [Test]
    public void Apply_ReReadsTheCurrentValueAtApplyTime_NotTheStaleValueFromStagingTime() {
        var tableDbis = new Dictionary<uint, uint> { [TableId] = widgetsDbi };
        var candidates = new[] { new EvictionCandidate(TableId, [1]) };

        var result = EvictionBatchApplier.Apply(env, tableDbis, candidates, (_, _) => [123]);

        Assert.That(result.IsOk(), Is.True);
        Assert.That(ReadRaw([1]), Is.EqualTo(new byte[] { 123 }),
            "readCurrentValue is invoked at apply time, on purpose - a row re-written after staging must write through with its current value.");
    }

    [Test]
    public void Apply_WhenAPutFails_LeavesEveryCandidateUnwrittenNotPartiallyApplied() {
        const uint neverOpenedDbi = 999;
        var tableDbis = new Dictionary<uint, uint> { [TableId] = neverOpenedDbi };
        var candidates = new[] { new EvictionCandidate(TableId, [1]) };

        var result = EvictionBatchApplier.Apply(env, tableDbis, candidates, (_, key) => key);

        Assert.That(result.IsError(), Is.True);
        Assert.That(ReadRaw([1]), Is.Null, "A failed batch must leave the row's mdbx copy untouched - the row stays resident, not partially evicted.");
    }

    [Test]
    public void Apply_CalledTwiceWithTheSameCandidate_IsIdempotentAndSafe() {
        var tableDbis = new Dictionary<uint, uint> { [TableId] = widgetsDbi };
        var candidates = new[] { new EvictionCandidate(TableId, [1]) };

        var first = EvictionBatchApplier.Apply(env, tableDbis, candidates, (_, _) => [7]);
        var second = EvictionBatchApplier.Apply(env, tableDbis, candidates, (_, _) => [7]);

        Assert.That(first.IsOk(), Is.True);
        Assert.That(second.IsOk(), Is.True,
            "A caller retrying a batch after an uncertain crash (committed but memory-drop status unknown) must be able to safely repeat it.");
        Assert.That(ReadRaw([1]), Is.EqualTo(new byte[] { 7 }));
    }
}
