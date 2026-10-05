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
        var frame = WalRecordCodec.Encode(7, WalEntryKind.CheckpointMarker, Array.Empty<WalChange>());

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
        var frame3 = WalRecordCodec.Encode(3, WalEntryKind.CheckpointMarker, Array.Empty<WalChange>());
        var buffer = frame1.Concat(frame2).Concat(frame3).ToArray();

        var result = WalRecordCodec.Scan(buffer);

        Assert.That(result.Status, Is.EqualTo(WalScanStatus.Clean));
        Assert.That(result.ValidLength, Is.EqualTo(buffer.Length));
        Assert.That(result.Entries.Select(e => e.Lsn), Is.EqualTo(new ulong[] { 1, 2, 3 }));
    }

    [Test]
    public void Scan_BufferTruncatedBeforeTheFixedHeaderCompletes_ReportsTornTailAtThatEntrysStart() {
        var frame1 = WalRecordCodec.Encode(1, WalEntryKind.Operation, OneChange());
        var buffer = frame1.Concat(new byte[] { 1, 2, 3 }).ToArray(); // fewer than HeaderSize trailing bytes

        var result = WalRecordCodec.Scan(buffer);

        Assert.That(result.Status, Is.EqualTo(WalScanStatus.TornTail));
        Assert.That(result.ValidLength, Is.EqualTo(frame1.Length));
        Assert.That(result.Entries.Select(e => e.Lsn), Is.EqualTo(new ulong[] { 1 }));
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
    public void Encode_FromListOverload_ProducesByteIdenticalFrameToTheArrayOverload() {
        var changes = TwoChangesAcrossTables();

        var frameFromArray = WalRecordCodec.Encode(42, WalEntryKind.Operation, changes);
        var frameFromList = WalRecordCodec.Encode(42, WalEntryKind.Operation, changes.ToList());

        Assert.That(frameFromList, Is.EqualTo(frameFromArray),
            "ColdStore.EndScope passes its change-tracking List directly (no ToArray() copy) - " +
            "this only stays correct if MemoryPack encodes List<WalChange> and WalChange[] identically.");
    }

    [Test]
    public void Encode_FromListOverload_WithDependencies_IsByteIdenticalToTheArrayOverload_AndRoundTripsThem() {
        var changes = TwoChangesAcrossTables();
        Guid[] dependsOn = [Guid.NewGuid(), Guid.NewGuid()];

        var frameFromArray = WalRecordCodec.Encode(42, WalEntryKind.Operation, changes, dependsOn: dependsOn);
        var frameFromList = WalRecordCodec.Encode(42, WalEntryKind.Operation, changes.ToList(), dependsOn: dependsOn);
        WalRecordCodec.TryDecode(frameFromList, out var entry, out _);

        Assert.That(frameFromList, Is.EqualTo(frameFromArray));
        Assert.That(entry.DependsOn, Is.EqualTo(dependsOn));
        Assert.That(entry.Changes, Has.Length.EqualTo(2));
    }

    [Test]
    public void EncodeThenTryDecode_FromListOverload_RoundTripsTheSameAsTheArrayOverload() {
        var frame = WalRecordCodec.Encode(42, WalEntryKind.Operation, TwoChangesAcrossTables().ToList());

        var status = WalRecordCodec.TryDecode(frame, out var entry, out var consumed);

        Assert.That(status, Is.EqualTo(WalScanStatus.Clean));
        Assert.That(consumed, Is.EqualTo(frame.Length));
        Assert.That(entry.Changes, Has.Length.EqualTo(2));
        Assert.That(entry.Changes[0].Key, Is.EqualTo(new byte[] { 1 }));
        Assert.That(entry.Changes[1].Row, Is.Null);
    }

    [Test]
    public void Encode_FromListOverload_EmptyList_MatchesEmptyArrayOverload() {
        var frameFromArray = WalRecordCodec.Encode(7, WalEntryKind.CheckpointMarker, Array.Empty<WalChange>());
        var frameFromList = WalRecordCodec.Encode(7, WalEntryKind.CheckpointMarker, new List<WalChange>());

        Assert.That(frameFromList, Is.EqualTo(frameFromArray));
    }

    [Test]
    public void Scan_EmptyBuffer_IsCleanWithNoEntries() {
        var result = WalRecordCodec.Scan([]);

        Assert.That(result.Status, Is.EqualTo(WalScanStatus.Clean));
        Assert.That(result.Entries, Is.Empty);
        Assert.That(result.ValidLength, Is.EqualTo(0));
    }

    // ---- the timestamp is an approximation that LOCATES an LSN; the encoder stays pure ----

    [Test]
    public void Encode_WithAnExplicitTimestamp_DecodesBackToTheSameValue() {
        const long stamp = 638_883_456_000_000_000L;
        var frame = WalRecordCodec.Encode(42, WalEntryKind.Operation, TwoChangesAcrossTables(), stamp);

        var status = WalRecordCodec.TryDecode(frame, out var entry, out var consumed);

        Assert.That(status, Is.EqualTo(WalScanStatus.Clean));
        Assert.That(consumed, Is.EqualTo(frame.Length));
        Assert.That(entry.Lsn, Is.EqualTo(42));
        Assert.That(entry.UtcTicks, Is.EqualTo(stamp));
    }

    [Test]
    public void Encode_ByDefault_LeavesTheFrameUnstampedAndDeterministic() {
        // Defaulting to Unstamped rather than "capture now" is what keeps the encoder pure:
        // two encodes of the same input must produce identical bytes, whatever the wall clock
        // is doing. Time enters the system at the WAL, which stamps in AppendOnly.
        var first = WalRecordCodec.Encode(42, WalEntryKind.Operation, TwoChangesAcrossTables());
        var second = WalRecordCodec.Encode(42, WalEntryKind.Operation, TwoChangesAcrossTables());

        Assert.That(second, Is.EqualTo(first));
        Assert.That(WalRecordCodec.Unstamped, Is.EqualTo(0L));
        Assert.That(WalRecordCodec.TryDecode(first, out var entry, out _), Is.EqualTo(WalScanStatus.Clean));
        Assert.That(entry.UtcTicks, Is.EqualTo(WalRecordCodec.Unstamped));
    }

    [Test]
    public void TryDecode_StampsSurviveAcrossAWholeScanInOrder() {
        var first = WalRecordCodec.Encode(1, WalEntryKind.Operation, OneChange(), 1000L);
        var second = WalRecordCodec.Encode(2, WalEntryKind.Operation, OneChange(), 2000L);
        var third = WalRecordCodec.Encode(3, WalEntryKind.Operation, OneChange(), 3000L);
        var buffer = first.Concat(second).Concat(third).ToArray();

        var scan = WalRecordCodec.Scan(buffer);

        Assert.That(scan.Status, Is.EqualTo(WalScanStatus.Clean));
        Assert.That(scan.Entries.Select(e => e.Lsn), Is.EqualTo(new ulong[] { 1, 2, 3 }));
        Assert.That(scan.Entries.Select(e => e.UtcTicks), Is.EqualTo(new ulong[] { 1000L, 2000L, 3000L }));
    }

    [Test]
    public void TryDecode_HeaderCarriesTheTimestampSoTheCrcCoversIt() {
        // The timestamp sits inside the CRC32 region, so a corrupted stamp is DETECTED rather
        // than silently yielding a wrong answer to a time-range query.
        var frame = WalRecordCodec.Encode(1, WalEntryKind.Operation, OneChange(), 1234L);
        frame[18] ^= 0xFF;

        var status = WalRecordCodec.TryDecode(frame, out _, out _);

        Assert.That(status, Is.Not.EqualTo(WalScanStatus.Clean));
    }
}