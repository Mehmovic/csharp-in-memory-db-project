using RhinoDB.Core;
using RhinoDB.Lib.Tables;

namespace RhinoDB.Lib.Durability.Test;

// WalArchive is what makes genesis replay possible at all: WriteAheadLog.Truncate() destroys
// everything it wipes, so archived segments are the only place history older than "since the
// last checkpoint" survives. Real file I/O against a real temp directory, no mocking layer
// (matching this project's established convention for WAL/ColdStore tests). ReadHistory takes
// the live WAL tail as data (ColdStore.PendingWalTail in production) rather than reading wal.dat
// itself - the live file is held open with FileShare.None by whichever ColdStore owns it, so a
// second reader in the same process would be a sharing violation. Dedup (a re-archived segment
// from a crash retry) is the load-bearing contract here - NOT strict LSN contiguity: an
// Instant-table-only transaction draws an LSN from the shared per-Db LsnSequence but never stages
// a WAL entry, so a gap between consecutive LSNs is expected and benign, not corruption (see
// WalArchive.cs's MergeInOrder).
public class WalArchiveTests {
    private string dir = "";
    private string archiveDir = "";

    [SetUp]
    public void SetUp() {
        dir = Path.Combine(Path.GetTempPath(), "rhinodb-wal-archive-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        archiveDir = Path.Combine(dir, WalArchive.ArchiveDirectoryName);
    }

    [TearDown]
    public void TearDown() {
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    static private DecodedWalEntry Entry(long lsn, long key = 1) =>
        new(lsn, WalEntryKind.Operation, [new WalChange(1, ChangeKind.Insert, BitConverter.GetBytes(key), [9])]);

    [Test]
    public void WriteSegment_WithEmptyEntries_CreatesNoFile() {
        var error = WalArchive.WriteSegment(archiveDir, Guid.NewGuid(), [], 0);

        Assert.That(error, Is.Null);
        Assert.That(Directory.Exists(archiveDir), Is.False);
    }

    [Test]
    public void WriteSegment_ThenReadHistory_RoundTripsAllEntries() {
        var error = WalArchive.WriteSegment(archiveDir, Guid.NewGuid(), [Entry(1), Entry(2), Entry(3)], 0);

        Assert.That(error, Is.Null);
        var history = WalArchive.ReadHistory(dir, [], 0).Unwrap();
        Assert.That(history.Select(e => e.Entry.Lsn), Is.EqualTo(new long[] { 1, 2, 3 }));
    }

    [Test]
    public void WriteSegment_CalledMultipleTimes_CreatesSequentiallyNumberedSegments() {
        WalArchive.WriteSegment(archiveDir, Guid.NewGuid(), [Entry(1)], 0);
        WalArchive.WriteSegment(archiveDir, Guid.NewGuid(), [Entry(2)], 0);

        var files = Directory.GetFiles(archiveDir).Select(Path.GetFileName).OrderBy(f => f).ToArray();
        Assert.That(files, Is.EqualTo(new[] { "00000001.wal", "00000002.wal" }));
    }

    [Test]
    public void ReadHistory_WithNothingArchivedAndNoLiveTail_ReturnsEmptyHistory() {
        var history = WalArchive.ReadHistory(dir, [], 0).Unwrap();

        Assert.That(history, Is.Empty);
    }

    [Test]
    public void ReadHistory_MergesArchivedSegmentsThenTheLiveTail_InLsnOrder() {
        WalArchive.WriteSegment(archiveDir, Guid.NewGuid(), [Entry(1), Entry(2)], 0);
        WalArchive.WriteSegment(archiveDir, Guid.NewGuid(), [Entry(3), Entry(4)], 0);

        var history = WalArchive.ReadHistory(dir, [Entry(5), Entry(6)], 0).Unwrap();

        Assert.That(history.Select(e => e.Entry.Lsn), Is.EqualTo(new long[] { 1, 2, 3, 4, 5, 6 }));
    }

    [Test]
    public void ReadHistory_ADuplicateSegmentFromACrashRetry_IsSkippedNotDuplicated() {
        // Simulates: checkpoint archived LSNs 1-2, then crashed before truncating the live WAL;
        // on restart the same span gets archived again into a second segment file.
        WalArchive.WriteSegment(archiveDir, Guid.NewGuid(), [Entry(1), Entry(2)], 0);
        WalArchive.WriteSegment(archiveDir, Guid.NewGuid(), [Entry(1), Entry(2)], 0);
        WalArchive.WriteSegment(archiveDir, Guid.NewGuid(), [Entry(3)], 0);

        var history = WalArchive.ReadHistory(dir, [], 0).Unwrap();

        Assert.That(history.Select(e => e.Entry.Lsn), Is.EqualTo(new long[] { 1, 2, 3 }));
    }

    [Test]
    public void ReadHistory_PrunedFromTheOldestEnd_StartsFromTheOldestSurvivingSegment() {
        // No special-casing needed: the first entry ever observed establishes the starting
        // point, whatever its LSN. Simulates a user deleting 00000001.wal by hand.
        WalArchive.WriteSegment(archiveDir, Guid.NewGuid(), [Entry(4), Entry(5)], 0);

        var history = WalArchive.ReadHistory(dir, [], 0).Unwrap();

        Assert.That(history.Select(e => e.Entry.Lsn), Is.EqualTo(new long[] { 4, 5 }));
    }

    [Test]
    public void ReadHistory_WithAGapBetweenSegments_TreatsItAsExpectedInstantOnlyLsnsNotCorruption() {
        // A gap here is indistinguishable, from LSNs alone, between "a segment was deleted from the
        // middle of the chain" and "LSNs 3-4 belonged to Instant-only transactions that never staged
        // a WAL entry" - the latter is the common case, so gaps are tolerated, not rejected.
        WalArchive.WriteSegment(archiveDir, Guid.NewGuid(), [Entry(1), Entry(2)], 0);
        WalArchive.WriteSegment(archiveDir, Guid.NewGuid(), [Entry(5), Entry(6)], 0);

        var history = WalArchive.ReadHistory(dir, [], 0).Unwrap();

        Assert.That(history.Select(e => e.Entry.Lsn), Is.EqualTo(new long[] { 1, 2, 5, 6 }));
    }

    [Test]
    public void ReadHistory_WithAGapBetweenTheArchiveAndTheLiveTail_TreatsItTheSameWay() {
        WalArchive.WriteSegment(archiveDir, Guid.NewGuid(), [Entry(1), Entry(2)], 0);

        var history = WalArchive.ReadHistory(dir, [Entry(5), Entry(6)], 0).Unwrap();

        Assert.That(history.Select(e => e.Entry.Lsn), Is.EqualTo(new long[] { 1, 2, 5, 6 }));
    }

    [Test]
    public void ReadHistory_TagsEachEntryWithItsOwnSegmentsGeneration_NotAUniformValue() {
        // Genesis replay (TableGenerator's generated LoadFromGenesis) needs to know which generation each
        // entry was WRITTEN under, not just the database's current one - a long-lived archive can span
        // several past generations, and each segment keeps whatever it was tagged with at write time.
        WalArchive.WriteSegment(archiveDir, Guid.NewGuid(), [Entry(1), Entry(2)], generation: 0);
        WalArchive.WriteSegment(archiveDir, Guid.NewGuid(), [Entry(3), Entry(4)], generation: 1);

        var history = WalArchive.ReadHistory(dir, [Entry(5)], liveGeneration: 2).Unwrap();

        Assert.That(history.Select(e => (e.Entry.Lsn, e.Generation)),
            Is.EqualTo(new[] { (1L, 0), (2L, 0), (3L, 1), (4L, 1), (5L, 2) }));
    }

    [Test]
    public void ReadOldestRetainedGeneration_WithNothingArchived_ReturnsNone() {
        Assert.That(WalArchive.ReadOldestRetainedGeneration(dir).Unwrap().IsNone(), Is.True);
    }

    [Test]
    public void ReadOldestRetainedGeneration_ReturnsTheMinimumAcrossEverySegment() {
        WalArchive.WriteSegment(archiveDir, Guid.NewGuid(), [Entry(1)], generation: 3);
        WalArchive.WriteSegment(archiveDir, Guid.NewGuid(), [Entry(2)], generation: 1);
        WalArchive.WriteSegment(archiveDir, Guid.NewGuid(), [Entry(3)], generation: 2);

        Assert.That(WalArchive.ReadOldestRetainedGeneration(dir).Unwrap().Get(), Is.EqualTo(1));
    }

    [Test]
    public void ReadOldestRetainedGeneration_OnlyReadsTheHeader_ACorruptedBodyDoesNotFailIt() {
        // The whole point of this method is being cheap - a pre-flight check that never scans/decodes a
        // segment's body. A torn/corrupted body (which WOULD fail ReadHistory) must not affect this at all.
        WalArchive.WriteSegment(archiveDir, Guid.NewGuid(), [Entry(1)], generation: 5);
        File.AppendAllText(Path.Combine(archiveDir, "00000001.wal"), "garbage-tail-bytes");

        Assert.That(WalArchive.ReadOldestRetainedGeneration(dir).Unwrap().Get(), Is.EqualTo(5));
    }

    [Test]
    public void DeleteSegmentsOlderThan_WithNothingArchived_IsANoOp() {
        Assert.That(WalArchive.DeleteSegmentsOlderThan(dir, 5).IsOk(), Is.True);
    }

    [Test]
    public void DeleteSegmentsOlderThan_DeletesOnlySegmentsStrictlyBelowTheTarget() {
        WalArchive.WriteSegment(archiveDir, Guid.NewGuid(), [Entry(1)], generation: 0);
        WalArchive.WriteSegment(archiveDir, Guid.NewGuid(), [Entry(2)], generation: 1);
        WalArchive.WriteSegment(archiveDir, Guid.NewGuid(), [Entry(3)], generation: 2);

        var result = WalArchive.DeleteSegmentsOlderThan(dir, 2);

        Assert.That(result.IsOk(), Is.True);
        var remainingHistory = WalArchive.ReadHistory(dir, [], 2).Unwrap();
        Assert.That(remainingHistory.Select(e => e.Generation), Is.EqualTo(new[] { 2 }),
            "generations 0 and 1 must be gone; generation 2 (at/above the target) must survive untouched.");
    }

    [Test]
    public void DeleteSegmentsOlderThan_CalledTwice_IsIdempotent() {
        WalArchive.WriteSegment(archiveDir, Guid.NewGuid(), [Entry(1)], generation: 0);
        WalArchive.WriteSegment(archiveDir, Guid.NewGuid(), [Entry(2)], generation: 2);

        var first = WalArchive.DeleteSegmentsOlderThan(dir, 2);
        var second = WalArchive.DeleteSegmentsOlderThan(dir, 2);

        Assert.That(first.IsOk(), Is.True);
        Assert.That(second.IsOk(), Is.True);
        Assert.That(WalArchive.ReadOldestRetainedGeneration(dir).Unwrap().Get(), Is.EqualTo(2));
    }

    [Test]
    public void ConsolidateSegments_WithNothingArchived_IsANoOp() {
        var result = WalArchive.ConsolidateSegments(dir, 2, static (_, _, _, key, row) => (key, row));

        Assert.That(result.IsOk(), Is.True);
    }

    [Test]
    public void ConsolidateSegments_TransformsBytesAndTagsTheSegmentWithTheTargetGeneration() {
        WalArchive.WriteSegment(archiveDir, Guid.NewGuid(), [Entry(1)], generation: 0);

        var result = WalArchive.ConsolidateSegments(dir, 2, static (_, _, _, key, row) => (key, row!.Select(b => (byte)(b + 1)).ToArray()));

        Assert.That(result.IsOk(), Is.True);
        var history = WalArchive.ReadHistory(dir, [], 2).Unwrap();
        Assert.That(history, Has.Count.EqualTo(1));
        Assert.That(history[0].Generation, Is.EqualTo(2));
        Assert.That(history[0].Entry.Changes[0].Row, Is.EqualTo(new byte[] { 10 }), "the row byte (9) must have gone through the transform.");
    }

    [Test]
    public void ConsolidateSegments_PreservesLsnAndChangeKind() {
        WalArchive.WriteSegment(archiveDir, Guid.NewGuid(), [Entry(7)], generation: 0);

        WalArchive.ConsolidateSegments(dir, 2, static (_, _, kind, key, row) => (key, row));

        var history = WalArchive.ReadHistory(dir, [], 2).Unwrap();
        Assert.That(history[0].Entry.Lsn, Is.EqualTo(7));
        Assert.That(history[0].Entry.Changes[0].Kind, Is.EqualTo(ChangeKind.Insert));
    }

    [Test]
    public void ConsolidateSegments_SegmentsAtOrAboveTheTarget_AreUntouched() {
        WalArchive.WriteSegment(archiveDir, Guid.NewGuid(), [Entry(1)], generation: 0);
        WalArchive.WriteSegment(archiveDir, Guid.NewGuid(), [Entry(2)], generation: 2);
        var transformCalls = new List<int>();

        WalArchive.ConsolidateSegments(dir, 2, (_, fromGeneration, _, key, row) => { transformCalls.Add(fromGeneration); return (key, row); });

        Assert.That(transformCalls, Is.EqualTo(new[] { 0 }), "only the segment strictly below the target must be transformed.");
        var history = WalArchive.ReadHistory(dir, [], 2).Unwrap();
        Assert.That(history.Select(e => e.Generation), Is.EqualTo(new[] { 2, 2 }));
    }

    [Test]
    public void ConsolidateSegments_WhenTheTransformReturnsNull_DropsThatChangeButKeepsTheRest() {
        var change1 = new WalChange(1, ChangeKind.Insert, [1], [9]);
        var change2 = new WalChange(2, ChangeKind.Insert, [2], [9]);
        var entry = new DecodedWalEntry(1, WalEntryKind.Operation, [change1, change2]);
        WalArchive.WriteSegment(archiveDir, Guid.NewGuid(), [entry], generation: 0);

        WalArchive.ConsolidateSegments(dir, 2, (tableId, _, _, key, row) => tableId == 1 ? (key, row) : null);

        var history = WalArchive.ReadHistory(dir, [], 2).Unwrap();
        Assert.That(history, Has.Count.EqualTo(1));
        Assert.That(history[0].Entry.Changes.Select(c => c.TableId), Is.EqualTo(new uint[] { 1 }));
    }

    [Test]
    public void ConsolidateSegments_WhenEveryChangeInASegmentIsDropped_DeletesItEntirely() {
        WalArchive.WriteSegment(archiveDir, Guid.NewGuid(), [Entry(1)], generation: 0);

        var result = WalArchive.ConsolidateSegments(dir, 2, static (_, _, _, _, _) => null);

        Assert.That(result.IsOk(), Is.True);
        Assert.That(Directory.GetFiles(archiveDir), Is.Empty);
    }

    [Test]
    public void ConsolidateSegments_KeepsTheSameFilenameSoOrdinalOrderIsPreserved() {
        WalArchive.WriteSegment(archiveDir, Guid.NewGuid(), [Entry(1)], generation: 0);
        WalArchive.WriteSegment(archiveDir, Guid.NewGuid(), [Entry(2)], generation: 0);
        var filesBefore = Directory.GetFiles(archiveDir).Select(Path.GetFileName).OrderBy(f => f).ToArray();

        WalArchive.ConsolidateSegments(dir, 2, static (_, _, _, key, row) => (key, row));

        var filesAfter = Directory.GetFiles(archiveDir).Select(Path.GetFileName).OrderBy(f => f).ToArray();
        Assert.That(filesAfter, Is.EqualTo(filesBefore));
    }

    [Test]
    public void ReadHistory_WithACorruptedArchivedSegment_FailsRatherThanTolerateIt() {
        // Unlike the live WAL, an archived segment is supposed to be closed/complete - a torn
        // or corrupted archived segment is real corruption, not an in-progress write.
        Directory.CreateDirectory(archiveDir);
        File.WriteAllBytes(Path.Combine(archiveDir, "00000001.wal"), [1, 2, 3]);

        var history = WalArchive.ReadHistory(dir, [], 0);

        Assert.That(history.IsError(), Is.True);
        Assert.That(history.GetError().Kind, Is.EqualTo(ErrorKind.WalCorrupted));
    }
}
