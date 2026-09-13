using RhinoDB.Lib.Durability;
using RhinoDB.Lib.Tables;

namespace RhinoDB.Lib.Durability.Test;

// WalRecordCodec is the on-disk entry format for the WAL (Docs/05-wal-design.md Phase 1,
// amended for the operation-level LSN resolution - one entry per operation, carrying every
// dirty Persistent table's changes for that operation as a single payload array). Pure
// codec-level tests, in isolation - no file I/O, no ColdStore wiring (that's a later stage).
public class WalRecordCodecTests {
    static private WalChange[] OneChange(uint tableId = 1) =>
        [new WalChange(tableId, ChangeKind.Insert, [1, 2, 3, 4], [9, 9, 9])];

    static private WalChange[] TwoChangesAcrossTables() =>
        [
            new WalChange(1, ChangeKind.Insert, [1], [10]),
            new WalChange(2, ChangeKind.Delete, [2], null),
        ];

    [Test]
    public void EncodeThenTryDecode_OperationEntry_RoundTripsLsnKindAndChanges() {
        var frame = WalRecordCodec.Encode(42, WalEntryKind.Operation, TwoChangesAcrossTables());

        var status = WalRecordCodec.TryDecode(frame, out var entry, out var consumed);

        Assert.That(status, Is.EqualTo(WalScanStatus.Clean));
        Assert.That(consumed, Is.EqualTo(frame.Length));
        Assert.That(entry.Lsn, Is.EqualTo(42));
        Assert.That(entry.Kind, Is.EqualTo(WalEntryKind.Operation));
        Assert.That(entry.Changes, Has.Length.EqualTo(2));
        Assert.That(entry.Changes[0].TableId, Is.EqualTo(1u));
        Assert.That(entry.Changes[0].Kind, Is.EqualTo(ChangeKind.Insert));
        Assert.That(entry.Changes[0].Key, Is.EqualTo(new byte[] { 1 }));
        Assert.That(entry.Changes[0].Row, Is.EqualTo(new byte[] { 10 }));
        Assert.That(entry.Changes[1].Row, Is.Null, "Delete changes carry no row payload.");
    }

    [Test]
    public void EncodeThenTryDecode_CheckpointMarkerEntry_RoundTripsWithNoChanges() {
        var frame = WalRecordCodec.Encode(7, WalEntryKind.CheckpointMarker, []);

        var status = WalRecordCodec.TryDecode(frame, out var entry, out var consumed);

        Assert.That(status, Is.EqualTo(WalScanStatus.Clean));
        Assert.That(consumed, Is.EqualTo(frame.Length));
        Assert.That(entry.Lsn, Is.EqualTo(7));
        Assert.That(entry.Kind, Is.EqualTo(WalEntryKind.CheckpointMarker));
        Assert.That(entry.Changes, Is.Empty);
    }

    [Test]
    public void Scan_MultipleValidEntriesBackToBack_ReturnsAllInOrderAsClean() {
        var frame1 = WalRecordCodec.Encode(1, WalEntryKind.Operation, OneChange());
        var frame2 = WalRecordCodec.Encode(2, WalEntryKind.Operation, OneChange());
        var frame3 = WalRecordCodec.Encode(3, WalEntryKind.CheckpointMarker, []);
        var buffer = frame1.Concat(frame2).Concat(frame3).ToArray();

        var result = WalRecordCodec.Scan(buffer);

        Assert.That(result.Status, Is.EqualTo(WalScanStatus.Clean));
        Assert.That(result.ValidLength, Is.EqualTo(buffer.Length));
        Assert.That(result.Entries.Select(e => e.Lsn), Is.EqualTo(new long[] { 1, 2, 3 }));
    }

    [Test]
    public void Scan_BufferTruncatedBeforeTheFixedHeaderCompletes_ReportsTornTailAtThatEntrysStart() {
        var frame1 = WalRecordCodec.Encode(1, WalEntryKind.Operation, OneChange());
        var buffer = frame1.Concat(new byte[] { 1, 2, 3 }).ToArray(); // fewer than HeaderSize trailing bytes

        var result = WalRecordCodec.Scan(buffer);

        Assert.That(result.Status, Is.EqualTo(WalScanStatus.TornTail));
        Assert.That(result.ValidLength, Is.EqualTo(frame1.Length));
        Assert.That(result.Entries.Select(e => e.Lsn), Is.EqualTo(new long[] { 1 }));
    }

    [Test]
    public void Scan_BufferTruncatedMidPayload_ReportsTornTailAtThatEntrysStart() {
        var frame1 = WalRecordCodec.Encode(1, WalEntryKind.Operation, OneChange());
        var frame2 = WalRecordCodec.Encode(2, WalEntryKind.Operation, TwoChangesAcrossTables());
        var buffer = frame1.Concat(frame2.Take(WalRecordCodec.HeaderSize + 2)).ToArray(); // header complete, payload cut short

        var result = WalRecordCodec.Scan(buffer);

        Assert.That(result.Status, Is.EqualTo(WalScanStatus.TornTail));
        Assert.That(result.ValidLength, Is.EqualTo(frame1.Length));
        Assert.That(result.Entries, Has.Count.EqualTo(1));
    }

    [Test]
    public void Scan_LastEntrysChecksumIsWrongAndNothingFollowsIt_TreatsItAsATornTail() {
        var frame1 = WalRecordCodec.Encode(1, WalEntryKind.Operation, OneChange());
        var frame2 = WalRecordCodec.Encode(2, WalEntryKind.Operation, OneChange());
        frame2[^1] ^= 0xFF; // corrupt the last byte of the last (tail) frame's payload
        var buffer = frame1.Concat(frame2).ToArray();

        var result = WalRecordCodec.Scan(buffer);

        Assert.That(result.Status, Is.EqualTo(WalScanStatus.TornTail),
            "A checksum failure with nothing after it looks exactly like a crash mid-write - tolerate it, don't refuse to open.");
        Assert.That(result.ValidLength, Is.EqualTo(frame1.Length));
        Assert.That(result.Entries, Has.Count.EqualTo(1));
    }

    [Test]
    public void Scan_ACorruptEntryHasAFurtherValidLookingEntryAfterIt_ReportsFatalCorruption() {
        var frame1 = WalRecordCodec.Encode(1, WalEntryKind.Operation, OneChange());
        var frame2 = WalRecordCodec.Encode(2, WalEntryKind.Operation, OneChange());
        frame2[^1] ^= 0xFF; // corrupt frame2's payload...
        var frame3 = WalRecordCodec.Encode(3, WalEntryKind.Operation, OneChange());
        var buffer = frame1.Concat(frame2).Concat(frame3).ToArray(); // ...but frame3 still follows it

        var result = WalRecordCodec.Scan(buffer);

        Assert.That(result.Status, Is.EqualTo(WalScanStatus.Corrupted),
            "Corruption with more data after it can't be a torn crash-tail - refuse to open, don't guess.");
        Assert.That(result.ValidLength, Is.EqualTo(frame1.Length));
        Assert.That(result.Entries, Has.Count.EqualTo(1), "Only the entries before the corrupt one are trustworthy.");
    }

    [Test]
    public void Scan_EmptyBuffer_IsCleanWithNoEntries() {
        var result = WalRecordCodec.Scan([]);

        Assert.That(result.Status, Is.EqualTo(WalScanStatus.Clean));
        Assert.That(result.Entries, Is.Empty);
        Assert.That(result.ValidLength, Is.EqualTo(0));
    }
}
