using RhinoDB.Core;
using RhinoDB.Lib.Tables;

namespace RhinoDB.Lib.Changes.Test;

public class ChangeRingBufferTests {
    static private byte[] Bytes(int value) => BitConverter.GetBytes(value);

    [Test]
    public void Record_ThenTryGetChangesSince_ReturnsTheRecordedEntry() {
        var ring = new ChangeRingBuffer(capacity: 8);

        ring.Record(tableId: 1, ChangeKind.Insert, lsn: 5, Bytes(1), Bytes(100));

        var result = ring.TryGetChangesSince(4);

        Assert.That(result.IsOk(), Is.True);
        using var entries = result.Unwrap();
        Assert.That(entries.Count, Is.EqualTo(1));
        var buffer = entries.Buffer();
        Assert.That(buffer[0].Lsn, Is.EqualTo(5));
        Assert.That(buffer[0].Change.TableId, Is.EqualTo(1u));
        Assert.That(buffer[0].Change.Kind, Is.EqualTo(ChangeKind.Insert));
        Assert.That(buffer[0].Change.Key, Is.EqualTo(Bytes(1)));
        Assert.That(buffer[0].Change.Row, Is.EqualTo(Bytes(100)));
    }

    [Test]
    public void TryGetChangesSince_OnAFreshRing_ReturnsEmptyNotAGap() {
        var ring = new ChangeRingBuffer(capacity: 8);

        var result = ring.TryGetChangesSince(0);

        Assert.That(result.IsOk(), Is.True);
        using var entries = result.Unwrap();
        Assert.That(entries.IsEmpty(), Is.True);
    }

    [Test]
    public void TryGetChangesSince_WhenCursorAlreadyMatchesTheNewestLsn_ReturnsEmpty() {
        var ring = new ChangeRingBuffer(capacity: 8);
        ring.Record(1, ChangeKind.Insert, lsn: 1, Bytes(1), null);
        ring.Record(1, ChangeKind.Insert, lsn: 2, Bytes(2), null);

        var result = ring.TryGetChangesSince(2);

        Assert.That(result.IsOk(), Is.True);
        using var entries = result.Unwrap();
        Assert.That(entries.IsEmpty(), Is.True);
    }

    [Test]
    public void TryGetChangesSince_WhenCursorIsAheadOfTheNewestLsn_ReturnsEmpty() {
        var ring = new ChangeRingBuffer(capacity: 8);
        ring.Record(1, ChangeKind.Insert, lsn: 1, Bytes(1), null);

        var result = ring.TryGetChangesSince(99);

        Assert.That(result.IsOk(), Is.True);
        using var entries = result.Unwrap();
        Assert.That(entries.IsEmpty(), Is.True);
    }

    [Test]
    public void Record_PastCapacity_OnlyTheNewestEntriesSurvive() {
        var ring = new ChangeRingBuffer(capacity: 4);
        for (var lsn = 0; lsn < 10; lsn++) ring.Record(1, ChangeKind.Insert, lsn, Bytes(lsn), null);
        // Oldest retained is lsn 6 (10 writes, capacity 4) - 5 is the boundary that still returns
        // everything retained without triggering a gap (see the dedicated boundary test below).

        var result = ring.TryGetChangesSince(5);

        Assert.That(result.IsOk(), Is.True);
        using var entries = result.Unwrap();
        var lsns = LsnsOf(entries);
        Assert.That(lsns, Is.EqualTo(new long[] { 6, 7, 8, 9 }));
    }

    [Test]
    public void TryGetChangesSince_ExactlyAtTheOldestRetainedLsnMinusOne_ReturnsEverythingRetained() {
        var ring = new ChangeRingBuffer(capacity: 4);
        for (var lsn = 0; lsn < 10; lsn++) ring.Record(1, ChangeKind.Insert, lsn, Bytes(lsn), null);
        // Oldest retained is lsn 6 (10 writes, capacity 4 -> 6,7,8,9 survive).

        var result = ring.TryGetChangesSince(5);

        Assert.That(result.IsOk(), Is.True);
        using var entries = result.Unwrap();
        Assert.That(LsnsOf(entries), Is.EqualTo(new long[] { 6, 7, 8, 9 }));
    }

    [Test]
    public void TryGetChangesSince_OlderThanTheOldestRetainedLsn_ReturnsRingBufferGap() {
        var ring = new ChangeRingBuffer(capacity: 4);
        for (var lsn = 0; lsn < 10; lsn++) ring.Record(1, ChangeKind.Insert, lsn, Bytes(lsn), null);
        // Oldest retained is lsn 6 - requesting anything since 4 means we're missing lsn 5's history.

        var result = ring.TryGetChangesSince(4);

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetError().Kind, Is.EqualTo(ErrorKind.RingBufferGap));
    }

    [Test]
    public void TryGetChangesSince_CursorBeforeTheFirstEverEntryOnARingThatHasNeverEvicted_IsNotAGap() {
        // Per-table ring buffers (task #84): a rarely-written table's ring can legitimately have its
        // very first-ever entry at a high LSN, simply because the table wasn't touched earlier - that's
        // not lost history. Only a ring that has actually evicted something (wrapped past capacity)
        // can have a genuine gap.
        var ring = new ChangeRingBuffer(capacity: 8);
        ring.Record(1, ChangeKind.Insert, lsn: 50, Bytes(1), null);

        var result = ring.TryGetChangesSince(-1);

        Assert.That(result.IsOk(), Is.True);
        using var entries = result.Unwrap();
        Assert.That(entries.Count, Is.EqualTo(1));
        Assert.That(entries.Buffer()[0].Lsn, Is.EqualTo(50));
    }

    [Test]
    public void Record_MultipleChangesSharingOneLsn_AllSurviveWithoutOverwritingEachOther() {
        // One transaction can touch several tables/changes and they all share one LSN (LSN is
        // per-transaction, not per-change) - the ring must not let same-LSN entries collide.
        var ring = new ChangeRingBuffer(capacity: 8);

        ring.Record(tableId: 1, ChangeKind.Insert, lsn: 7, Bytes(1), Bytes(100));
        ring.Record(tableId: 2, ChangeKind.Update, lsn: 7, Bytes(2), Bytes(200));
        ring.Record(tableId: 3, ChangeKind.Delete, lsn: 7, Bytes(3), null);

        var result = ring.TryGetChangesSince(6);

        Assert.That(result.IsOk(), Is.True);
        using var entries = result.Unwrap();
        Assert.That(entries.Count, Is.EqualTo(3));
        var buffer = entries.Buffer();
        Assert.That(buffer[0].Change.TableId, Is.EqualTo(1u));
        Assert.That(buffer[1].Change.TableId, Is.EqualTo(2u));
        Assert.That(buffer[2].Change.TableId, Is.EqualTo(3u));
        for (var i = 0; i < buffer.Length; i++) Assert.That(buffer[i].Lsn, Is.EqualTo(7));
    }

    static private long[] LsnsOf(ArrayPoolContainer<RingEntry> entries) {
        var buffer = entries.Buffer();
        var result = new long[buffer.Length];
        for (var i = 0; i < buffer.Length; i++) result[i] = buffer[i].Lsn;
        return result;
    }
}
