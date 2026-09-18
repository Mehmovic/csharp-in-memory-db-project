using RhinoDB.Core;
using RhinoDB.Lib.Tables;

namespace RhinoDB.Lib.Durability.Test;

// WalArchive is what makes genesis replay possible at all: WriteAheadLog.Truncate() destroys
// everything it wipes, so archived segments are the only place history older than "since the
// last checkpoint" survives. Real file I/O against a real temp directory, no mocking layer
// (matching this project's established convention for WAL/ColdStore tests). ReadHistory takes
// the live WAL tail as data (ColdStore.PendingWalTail in production) rather than reading wal.dat
// itself - the live file is held open with FileShare.None by whichever ColdStore owns it, so a
// second reader in the same process would be a sharing violation. Gap-vs-dedup behavior is the
// load-bearing contract here, not incidental - see the reasoning in WalArchive.cs itself.
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
        var error = WalArchive.WriteSegment(archiveDir, Guid.NewGuid(), []);

        Assert.That(error, Is.Null);
        Assert.That(Directory.Exists(archiveDir), Is.False);
    }

    [Test]
    public void WriteSegment_ThenReadHistory_RoundTripsAllEntries() {
        var error = WalArchive.WriteSegment(archiveDir, Guid.NewGuid(), [Entry(1), Entry(2), Entry(3)]);

        Assert.That(error, Is.Null);
        var history = WalArchive.ReadHistory(dir, []).Unwrap();
        Assert.That(history.Select(e => e.Lsn), Is.EqualTo(new long[] { 1, 2, 3 }));
    }

    [Test]
    public void WriteSegment_CalledMultipleTimes_CreatesSequentiallyNumberedSegments() {
        WalArchive.WriteSegment(archiveDir, Guid.NewGuid(), [Entry(1)]);
        WalArchive.WriteSegment(archiveDir, Guid.NewGuid(), [Entry(2)]);

        var files = Directory.GetFiles(archiveDir).Select(Path.GetFileName).OrderBy(f => f).ToArray();
        Assert.That(files, Is.EqualTo(new[] { "00000001.wal", "00000002.wal" }));
    }

    [Test]
    public void ReadHistory_WithNothingArchivedAndNoLiveTail_ReturnsEmptyHistory() {
        var history = WalArchive.ReadHistory(dir, []).Unwrap();

        Assert.That(history, Is.Empty);
    }

    [Test]
    public void ReadHistory_MergesArchivedSegmentsThenTheLiveTail_InLsnOrder() {
        WalArchive.WriteSegment(archiveDir, Guid.NewGuid(), [Entry(1), Entry(2)]);
        WalArchive.WriteSegment(archiveDir, Guid.NewGuid(), [Entry(3), Entry(4)]);

        var history = WalArchive.ReadHistory(dir, [Entry(5), Entry(6)]).Unwrap();

        Assert.That(history.Select(e => e.Lsn), Is.EqualTo(new long[] { 1, 2, 3, 4, 5, 6 }));
    }

    [Test]
    public void ReadHistory_ADuplicateSegmentFromACrashRetry_IsSkippedNotDuplicated() {
        // Simulates: checkpoint archived LSNs 1-2, then crashed before truncating the live WAL;
        // on restart the same span gets archived again into a second segment file.
        WalArchive.WriteSegment(archiveDir, Guid.NewGuid(), [Entry(1), Entry(2)]);
        WalArchive.WriteSegment(archiveDir, Guid.NewGuid(), [Entry(1), Entry(2)]);
        WalArchive.WriteSegment(archiveDir, Guid.NewGuid(), [Entry(3)]);

        var history = WalArchive.ReadHistory(dir, []).Unwrap();

        Assert.That(history.Select(e => e.Lsn), Is.EqualTo(new long[] { 1, 2, 3 }));
    }

    [Test]
    public void ReadHistory_PrunedFromTheOldestEnd_StartsFromTheOldestSurvivingSegment() {
        // No special-casing needed: the first entry ever observed establishes the starting
        // point, whatever its LSN. Simulates a user deleting 00000001.wal by hand.
        WalArchive.WriteSegment(archiveDir, Guid.NewGuid(), [Entry(4), Entry(5)]);

        var history = WalArchive.ReadHistory(dir, []).Unwrap();

        Assert.That(history.Select(e => e.Lsn), Is.EqualTo(new long[] { 4, 5 }));
    }

    [Test]
    public void ReadHistory_WithAGapBetweenSegments_FailsInsteadOfSilentlyReconstructingWrongState() {
        // Simulates a segment deleted from the middle of the chain, not the oldest end.
        WalArchive.WriteSegment(archiveDir, Guid.NewGuid(), [Entry(1), Entry(2)]);
        WalArchive.WriteSegment(archiveDir, Guid.NewGuid(), [Entry(5), Entry(6)]);

        var history = WalArchive.ReadHistory(dir, []);

        Assert.That(history.IsError(), Is.True);
        Assert.That(history.GetError().Kind, Is.EqualTo(ErrorKind.WalArchiveGap));
    }

    [Test]
    public void ReadHistory_WithAGapBetweenTheArchiveAndTheLiveTail_FailsTheSameWay() {
        WalArchive.WriteSegment(archiveDir, Guid.NewGuid(), [Entry(1), Entry(2)]);

        var history = WalArchive.ReadHistory(dir, [Entry(5), Entry(6)]);

        Assert.That(history.IsError(), Is.True);
        Assert.That(history.GetError().Kind, Is.EqualTo(ErrorKind.WalArchiveGap));
    }

    [Test]
    public void ReadHistory_WithACorruptedArchivedSegment_FailsRatherThanTolerateIt() {
        // Unlike the live WAL, an archived segment is supposed to be closed/complete - a torn
        // or corrupted archived segment is real corruption, not an in-progress write.
        Directory.CreateDirectory(archiveDir);
        File.WriteAllBytes(Path.Combine(archiveDir, "00000001.wal"), [1, 2, 3]);

        var history = WalArchive.ReadHistory(dir, []);

        Assert.That(history.IsError(), Is.True);
        Assert.That(history.GetError().Kind, Is.EqualTo(ErrorKind.WalCorrupted));
    }
}
