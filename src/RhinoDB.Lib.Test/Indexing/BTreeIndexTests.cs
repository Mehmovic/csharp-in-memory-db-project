using RhinoDB.Core.Exceptions;

namespace RhinoDB.Lib.Indexing.Test;

public class BTreeIndexTests {
    static private BTreeIndex<int> NewIndex() => new BTreeIndex<int>();

    static private int[] RangeOf(BTreeIndex<int> index, int from, int to) {
        using var writer = index.Range(from, to);
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

    static private BTreeIndex<int> NewScrambledIndex(int count) {
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
}
