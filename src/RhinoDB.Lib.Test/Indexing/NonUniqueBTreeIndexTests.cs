namespace RhinoDB.Lib.Indexing.Test;

public class NonUniqueBTreeIndexTests {
    static private NonUniqueBTreeIndex<int> NewIndex() => new NonUniqueBTreeIndex<int>();

    static private int[] RangeOf(NonUniqueBTreeIndex<int> index, int from, int to) {
        using var writer = index.Range(from, to);
        return writer.Buffer().ToArray();
    }

    static private int[] OffsetsOf(NonUniqueBTreeIndex<int> index, int key) {
        using var list = index.GetOffsets(key);
        return list.Buffer().ToArray();
    }

    [Test]
    public void Range_WithNoMatches_ReturnsEmpty() {
        var index = NewIndex();

        Assert.That(RangeOf(index, 1, 10), Is.Empty);
    }

    [Test]
    public void Insert_ThenRange_ReturnsTheInsertedOffset() {
        var index = NewIndex();

        index.Insert(30, 0);

        Assert.That(RangeOf(index, 30, 30), Is.EqualTo(new[] { 0 }));
    }

    [Test]
    public void Insert_MultipleOffsetsSameKey_RangeReturnsAllOfThem() {
        var index = NewIndex();

        index.Insert(30, 0);
        index.Insert(30, 1);

        Assert.That(RangeOf(index, 30, 30), Is.EquivalentTo(new[] { 0, 1 }));
    }

    [Test]
    public void Range_ReturnsOffsetsAcrossMultipleKeysInAscendingKeyOrder_DuplicatesGroupedByKey() {
        var index = NewIndex();
        // Inserted out of key order on purpose - ordering must come from the index
        // itself, not from insertion order.
        index.Insert(40, 5);
        index.Insert(20, 1);
        index.Insert(25, 2);
        index.Insert(25, 3);
        index.Insert(35, 4);

        Assert.That(RangeOf(index, 20, 35), Is.EqualTo(new[] { 1, 2, 3, 4 }));
    }

    [Test]
    public void Range_WhenFromIsGreaterThanTo_ReturnsEmpty() {
        var index = NewIndex();
        index.Insert(30, 0);

        Assert.That(RangeOf(index, 30, 1), Is.Empty);
    }

    [Test]
    public void Delete_RemovesOnlyTheGivenOffset_OtherOffsetsWithSameKeyRemain() {
        var index = NewIndex();
        index.Insert(30, 0);
        index.Insert(30, 1);

        index.Delete(30, 0);

        Assert.That(RangeOf(index, 30, 30), Is.EqualTo(new[] { 1 }));
    }

    [Test]
    public void Delete_LastOffsetUnderAKey_KeyNoLongerAppearsInRange() {
        var index = NewIndex();
        index.Insert(30, 0);

        index.Delete(30, 0);

        Assert.That(RangeOf(index, 1, 100), Is.Empty);
    }

    [Test]
    public void GetOffsets_ReturnsAllOffsetsForKey() {
        var index = NewIndex();
        index.Insert(30, 0);
        index.Insert(30, 1);

        Assert.That(OffsetsOf(index, 30), Is.EquivalentTo(new[] { 0, 1 }));
    }

    [Test]
    public void GetOffsets_UnknownKey_ReturnsEmpty() {
        var index = NewIndex();

        Assert.That(OffsetsOf(index, 30), Is.Empty);
    }

    [Test]
    public void Range_BoundaryKeysHaveMultipleEntries_AllOfThemIncluded() {
        var index = NewIndex();
        index.Insert(20, 1);
        index.Insert(20, 2);
        index.Insert(30, 3);
        index.Insert(40, 4);
        index.Insert(40, 5);

        Assert.That(RangeOf(index, 20, 40), Is.EquivalentTo(new[] { 1, 2, 3, 4, 5 }));
    }

    [Test]
    public void Delete_ThenInsertAgainUnderSameKey_Succeeds() {
        var index = NewIndex();
        index.Insert(30, 0);
        index.Delete(30, 0);

        index.Insert(30, 1);

        Assert.That(OffsetsOf(index, 30), Is.EqualTo(new[] { 1 }));
    }

    [Test]
    public void Insert_KeysOutOfOrder_MaintainsAscendingOrder() {
        var index = NewIndex();
        var ages = new[] { 50, 10, 60, 30, 20, 40 }; // out of order on purpose
        for (var i = 0; i < ages.Length; i++)
            index.Insert(ages[i], i);

        Assert.That(RangeOf(index, 10, 60), Has.Length.EqualTo(6));
    }

    // ---- Multi-chunk Range paths (the binary-search start) ----

    static private NonUniqueBTreeIndex<int> NewScrambledIndex(int count) {
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
    public void Range_FromBelowEveryKey_ReturnsTheWholeIndex() {
        var index = NewScrambledIndex(601);

        Assert.That(RangeOf(index, -1, 600), Is.EqualTo(Enumerable.Range(0, 601).Select(k => k * 10).ToArray()));
    }

    // ---- Duplicate keys spanning more than one chunk ----

    [Test]
    public void Range_DuplicateRunFillingMoreThanOneChunk_ReturnsEveryOffset() {
        var index = NewIndex();
        // More duplicates than a single chunk holds, so the run must span
        // at least one chunk boundary.
        for (var i = 0; i < 1000; i++)
            index.Insert(7, i);

        var offsets = RangeOf(index, 7, 7);

        Assert.That(offsets, Has.Length.EqualTo(1000));
        Assert.That(offsets, Is.EquivalentTo(Enumerable.Range(0, 1000).ToArray()));
    }

    [Test]
    public void Range_WhenFromEqualsAKeyWithLoadsOfDuplicates_IncludesEveryDuplicate() {
        var index = NewIndex();
        for (var i = 0; i < 600; i++)
            index.Insert(7, i);
        index.Insert(8, 800);

        // 'from' is the duplicated key itself - the walk-back plus the forward
        // scan must pick the duplicates up across every chunk they landed in.
        var offsets = RangeOf(index, 7, 8);

        Assert.That(offsets, Has.Length.EqualTo(601));
        Assert.That(offsets, Does.Contain(800));
    }

    [Test]
    public void Range_DuplicatesOnAKeyStraddlingAChunkBoundary_ReturnsOtherKeysToo() {
        var index = NewIndex();
        for (var i = 0; i < 600; i++)
            index.Insert(7, 7000 + i);
        index.Insert(5, 500);
        index.Insert(9, 900);

        var offsets = RangeOf(index, 0, 100);

        Assert.That(offsets, Has.Length.EqualTo(602));
        Assert.That(offsets, Does.Contain(500));
        Assert.That(offsets, Does.Contain(900));
        Assert.That(offsets.Take(2).ToArray(), Is.EqualTo(new[] { 500, 7000 }));
    }

    [Test]
    public void GetOffsets_ForKeyWithDuplicatesAcrossChunks_ReturnsEveryOffset() {
        var index = NewIndex();
        for (var i = 0; i < 600; i++)
            index.Insert(7, i);

        var offsets = OffsetsOf(index, 7);

        Assert.That(offsets, Has.Length.EqualTo(600));
        Assert.That(offsets, Is.EquivalentTo(Enumerable.Range(0, 600).ToArray()));
    }

    [Test]
    public void Delete_OffsetInALaterChunkOfADuplicateRun_RemovesOnlyThatOffset() {
        var index = NewIndex();
        // 600 duplicates of one key: the run spans several chunks.
        for (var i = 0; i < 600; i++)
            index.Insert(7, i);
        index.Insert(8, 800);

        index.Delete(7, 599); // sits in a later chunk of the duplicate run

        var remaining = OffsetsOf(index, 7);
        Assert.That(remaining, Has.Length.EqualTo(599));
        Assert.That(remaining, Does.Not.Contain(599));
        Assert.That(remaining, Is.EquivalentTo(Enumerable.Range(0, 599).ToArray()));

        var range = RangeOf(index, 7, 8);
        Assert.That(range, Does.Contain(800));
        Assert.That(range, Has.Length.EqualTo(600));
    }

    [Test]
    public void Range_AtScale_DuplicateRunsAcrossTensOfChunks_ReturnEveryOffset() {
        var index = NewIndex();
        // Keys 0..999 with ten duplicates each => ~40 chunks, so most duplicate
        // runs are not in the chunk the search starts at.
        for (var i = 0; i < 10_000; i++)
            index.Insert(i / 10, i);

        Assert.That(RangeOf(index, 500, 500), Is.EquivalentTo(Enumerable.Range(5_000, 10).ToArray()));
        Assert.That(RangeOf(index, 400, 499), Is.EquivalentTo(Enumerable.Range(4_000, 1_000).ToArray()));
        Assert.That(OffsetsOf(index, 999), Is.EquivalentTo(Enumerable.Range(9_990, 10).ToArray()));
        Assert.That(RangeOf(index, 0, 999), Has.Length.EqualTo(10_000));
    }
}
