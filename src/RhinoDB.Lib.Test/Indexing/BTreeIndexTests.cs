using RhinoDB.Core.Exceptions;

namespace RhinoDB.Lib.Indexing.Test;

public class BTreeIndexTests {
    static private BTreeIndex<int, DefaultComparer<int>> NewIndex() => new BTreeIndex<int, DefaultComparer<int>>();

    static private int[] RangeOf(BTreeIndex<int, DefaultComparer<int>> index, int from, int to) {
        using var writer = index.GetOffsetsRange(from, to);
        return writer.Buffer().ToArray();
    }

    [Test]
    public void Insert_ThenGetOffset_ReturnsTheOffset() {
        var index = NewIndex();

        index.Insert(1, 0);

        Assert.That(index.GetOffset(1).Unwrap(), Is.EqualTo(0));
        Assert.That(index.Count, Is.EqualTo(1));
    }

    [Test]
    public void Insert_MultipleKeys_AllRetrievableByKey() {
        var index = NewIndex();

        index.Insert(1, 0);
        index.Insert(2, 1);
        index.Insert(3, 2);

        Assert.That(index.GetOffset(1).Unwrap(), Is.EqualTo(0));
        Assert.That(index.GetOffset(2).Unwrap(), Is.EqualTo(1));
        Assert.That(index.GetOffset(3).Unwrap(), Is.EqualTo(2));
        Assert.That(index.Count, Is.EqualTo(3));
    }

    [Test]
    public void GetOffset_UnknownKey_ReturnsFailureWithIndexKeyNotFoundException() {
        var index = NewIndex();

        var result = index.GetOffset(999);

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetError().ToException(), Is.InstanceOf<IndexKeyNotFoundException>());
    }

    [Test]
    public void Delete_RemovesTheKey_SubsequentGetOffsetFails() {
        var index = NewIndex();
        index.Insert(1, 0);

        index.Delete(1);

        Assert.That(index.Count, Is.EqualTo(0));
        Assert.That(index.GetOffset(1).IsError(), Is.True);
    }

    [Test]
    public void Delete_ThenInsertSameKeyAgain_Succeeds() {
        var index = NewIndex();
        index.Insert(1, 0);
        index.Delete(1);

        index.Insert(1, 5);

        Assert.That(index.GetOffset(1).Unwrap(), Is.EqualTo(5));
        Assert.That(index.Count, Is.EqualTo(1));
    }

    [Test]
    public void DeleteThenInsertNewKey_SameOffset_SupportsRekeying() {
        var index = NewIndex();
        index.Insert(1, 0);

        index.Delete(1);
        index.Insert(2, 0);

        Assert.That(index.GetOffset(1).IsError(), Is.True);
        Assert.That(index.GetOffset(2).Unwrap(), Is.EqualTo(0));
    }

    [Test]
    public void Range_ReturnsOffsetsWithinBoundsInclusive_InAscendingKeyOrder() {
        var index = NewIndex();
        // Inserted out of key order on purpose - ordering must come from the index
        // itself, not from insertion order.
        index.Insert(5, 50);
        index.Insert(1, 10);
        index.Insert(3, 30);
        index.Insert(4, 40);
        index.Insert(2, 20);

        Assert.That(RangeOf(index, 2, 4), Is.EqualTo(new[] { 20, 30, 40 }));
    }

    [Test]
    public void Range_BoundsAreInclusive() {
        var index = NewIndex();
        index.Insert(1, 10);
        index.Insert(2, 20);
        index.Insert(3, 30);

        Assert.That(RangeOf(index, 1, 3), Is.EqualTo(new[] { 10, 20, 30 }));
    }

    [Test]
    public void Range_WithNoMatches_ReturnsEmpty() {
        var index = NewIndex();
        index.Insert(1, 10);

        Assert.That(RangeOf(index, 100, 200), Is.Empty);
    }

    [Test]
    public void Range_WhenFromIsGreaterThanTo_ReturnsEmpty() {
        var index = NewIndex();
        index.Insert(1, 10);
        index.Insert(2, 20);

        Assert.That(RangeOf(index, 2, 1), Is.Empty);
    }

    // ---- Multi-chunk Range paths (the binary-search start) ----

    static private BTreeIndex<int, DefaultComparer<int>> NewScrambledIndex(int count) {
        // (i * 677) % 601 is a permutation of 0..600 - scrambled insertion order,
        // forcing chunk splits, without any randomness.
        var index = NewIndex();
        for (var i = 0; i < count; i++) {
            var key = i * 677 % 601;
            index.Insert(key, key * 10);
        }
        return index;
    }

    [Test]
    public void Range_AcrossManyChunks_ReturnsEveryOffsetInKeyOrder() {
        var index = NewScrambledIndex(601);

        Assert.That(RangeOf(index, 200, 300), Is.EqualTo(Enumerable.Range(200, 101).Select(k => k * 10).ToArray()));
    }

    [Test]
    public void Range_PointQueryFarFromTheFirstChunk_ReturnsJustThatOffset() {
        var index = NewScrambledIndex(601);

        Assert.That(RangeOf(index, 450, 450), Is.EqualTo(new[] { 4500 }));
    }

    [Test]
    public void Range_WhenFromFallsInAKeyGap_ReturnsOnlyTheKeysAboveIt() {
        // Walk the whole permutation of 0..600 and keep only the even keys, so the
        // index holds exactly the even keys and every odd key is a gap.
        var index = NewIndex();
        for (var i = 0; i < 601; i++) {
            var key = (i * 677) % 601;
            if (key % 2 == 0) index.Insert(key, key * 10);
        }

        Assert.That(RangeOf(index, 399, 401), Is.EqualTo(new[] { 4000 }));
    }

    [Test]
    public void Range_ReflectsStateAfterADelete() {
        var index = NewIndex();
        index.Insert(1, 10);
        index.Insert(2, 20);
        index.Insert(3, 30);

        index.Delete(2);

        Assert.That(RangeOf(index, 1, 3), Is.EqualTo(new[] { 10, 30 }));
    }

    [Test]
    public void Range_FromEqualsTo_OnExistingKey_ReturnsThatSingleOffset() {
        var index = NewIndex();
        index.Insert(1, 10);
        index.Insert(2, 20);

        Assert.That(RangeOf(index, 2, 2), Is.EqualTo(new[] { 20 }));
    }

    [Test]
    public void Insert_KeysOutOfOrder_RangeStillReturnsAscendingOrder() {
        var index = NewIndex();
        index.Insert(3, 300);
        index.Insert(1, 100);
        index.Insert(2, 200);

        Assert.That(RangeOf(index, 1, 3), Is.EqualTo(new[] { 100, 200, 300 }));
    }

    // ---- Merge-on-underflow (chunks consolidate back together after delete-heavy churn) ----

    [Test]
    public void Delete_ScatteredAcrossManyChunks_LeavingThemUnderQuarterCapacity_MergesChunksBackTogether() {
        var index = new BTreeIndex<int, DefaultComparer<int>>(chunkSize: 16); // chunkCapacity 16, merge threshold 4
        for (var key = 0; key < 1_000; key++) index.Insert(key, key);
        var chunksBeforeChurn = index.ChunkCount;

        // Keep only every 8th key - every chunk ends up far under quarter capacity.
        for (var key = 0; key < 1_000; key++) {
            if (key % 8 != 0) index.Delete(key);
        }

        Assert.That(index.ChunkCount, Is.LessThan(chunksBeforeChurn),
            "Chunks must consolidate back together once they're sparsely populated, not stay fragmented forever.");
        Assert.That(index.Count, Is.EqualTo(125), "Only the surviving (every-8th) keys should remain.");

        for (var key = 0; key < 1_000; key += 8)
            Assert.That(index.GetOffset(key).Unwrap(), Is.EqualTo(key), $"Key {key} must still be retrievable after merging.");
        Assert.That(RangeOf(index, 0, 999), Is.EqualTo(Enumerable.Range(0, 125).Select(i => i * 8).ToArray()),
            "A full range scan after merging must still return every surviving key, in order, with none lost or duplicated.");
    }

    [Test]
    public void Delete_DownToASingleSurvivingChunk_LeavesExactlyOneChunk() {
        var index = new BTreeIndex<int, DefaultComparer<int>>(chunkSize: 16);
        for (var key = 0; key < 500; key++) index.Insert(key, key);

        for (var key = 1; key < 500; key++) index.Delete(key); // leave only key 0

        Assert.That(index.ChunkCount, Is.EqualTo(1));
        Assert.That(index.Count, Is.EqualTo(1));
        Assert.That(index.GetOffset(0).Unwrap(), Is.EqualTo(0));
    }

    [Test]
    public void Delete_EveryKey_LeavesOneEmptyChunkNotZero() {
        var index = new BTreeIndex<int, DefaultComparer<int>>(chunkSize: 16);
        for (var key = 0; key < 500; key++) index.Insert(key, key);

        for (var key = 0; key < 500; key++) index.Delete(key);

        Assert.That(index.Count, Is.EqualTo(0));
        Assert.That(index.ChunkCount, Is.EqualTo(1), "A drained index still needs one (empty) chunk to insert back into.");
    }

    [Test]
    public void Range_AtScale_TensOfChunks_ReturnsTheExactWindow() {
        var index = NewIndex();
        // 10_000 keys against a 256-entry chunk size => ~40 chunks, so the
        // start-chunk search really is a binary search over many chunks.
        for (var key = 0; key < 10_000; key++)
            index.Insert(key, key);

        Assert.That(RangeOf(index, 4_000, 5_000), Is.EqualTo(Enumerable.Range(4_000, 1_001).ToArray()));
        Assert.That(RangeOf(index, 9_999, 9_999), Is.EqualTo(new[] { 9_999 }));
        Assert.That(RangeOf(index, 0, 9_999), Is.EqualTo(Enumerable.Range(0, 10_000).ToArray()));
    }

    // ---- Append-at-the-end split (auto-increment primary keys) ----

    [Test]
    public void Insert_Ascending_SplitsAtTheEnd_LeavingEveryChunkFull() {
        // An ascending key always lands past the last key of the last chunk. Splitting that
        // chunk in half would leave every chunk behind it half-empty forever; the append split
        // starts a fresh chunk with just the new key instead, so chunks stay packed.
        var index = new BTreeIndex<int, DefaultComparer<int>>(chunkSize: 16);
        for (var key = 0; key < 1_600; key++) index.Insert(key, key);

        Assert.That(index.ChunkCount, Is.EqualTo(100), "1,600 ascending keys into 16-entry chunks must fill exactly 100 chunks");
        Assert.That(RangeOf(index, 0, 1_599), Is.EqualTo(Enumerable.Range(0, 1_600).ToArray()));
    }

    [Test]
    public void Insert_IntoAFullMiddleChunk_StillSplitsInHalf() {
        var index = new BTreeIndex<int, DefaultComparer<int>>(chunkSize: 16);
        for (var key = 0; key < 64; key += 2) index.Insert(key, key); // two full chunks of even keys

        index.Insert(5, 5); // lands inside the first (full) chunk, not at the end

        Assert.That(index.ChunkCount, Is.EqualTo(3));
        Assert.That(index.GetOffset(5).Unwrap(), Is.EqualTo(5));
        Assert.That(RangeOf(index, 0, 63), Is.EqualTo(Enumerable.Range(0, 32).Select(i => i * 2).Append(5).Order().ToArray()));
    }
}
