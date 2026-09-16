namespace RhinoDB.Lib.Indexing.Test;

public class NonUniqueBTreeIndexTests {
    static private NonUniqueBTreeIndex<int> NewIndex() => new NonUniqueBTreeIndex<int>();

    [Test]
    public void Range_WithNoMatches_ReturnsEmpty() {
        var offsets = new List<int>();
        var index = NewIndex();
        index.Range(1, 10, offsets);

        Assert.That(offsets, Is.Empty);
    }

    [Test]
    public void Insert_ThenRange_ReturnsTheInsertedOffset() {
        var offsets = new List<int>();
        var index = NewIndex();

        index.Insert(30, 0);
        index.Range(30, 30, offsets);

        Assert.That(offsets, Is.EqualTo(new[] { 0 }));
    }

    [Test]
    public void Insert_MultipleOffsetsSameKey_RangeReturnsAllOfThem() {
        var offsets = new List<int>();
        var index = NewIndex();

        index.Insert(30, 0);
        index.Insert(30, 1);

        index.Range(30, 30, offsets);

        Assert.That(offsets, Is.EquivalentTo(new[] { 0, 1 }));
    }

    [Test]
    public void Range_ReturnsOffsetsAcrossMultipleKeysInAscendingKeyOrder_DuplicatesGroupedByKey() {
        var offsets = new List<int>();
        var index = NewIndex();
        // Inserted out of key order on purpose - ordering must come from the index
        // itself, not from insertion order.
        index.Insert(40, 5);
        index.Insert(20, 1);
        index.Insert(25, 2);
        index.Insert(25, 3);
        index.Insert(35, 4);

        index.Range(20, 35, offsets);

        Assert.That(offsets, Is.EqualTo(new[] { 1, 2, 3, 4 }));
    }

    [Test]
    public void Range_WhenFromIsGreaterThanTo_ReturnsEmpty() {
        var offsets = new List<int>();
        var index = NewIndex();
        index.Insert(30, 0);
        index.Range(30, 1, offsets);
        
        Assert.That(offsets, Is.Empty);
    }

    [Test]
    public void Delete_RemovesOnlyTheGivenOffset_OtherOffsetsWithSameKeyRemain() {
        var offsets = new List<int>();
        var index = NewIndex();
        index.Insert(30, 0);
        index.Insert(30, 1);

        index.Delete(30, 0);

        index.Range(30, 30, offsets);
        
        Assert.That(offsets, Is.EqualTo(new[] { 1 }));
    }

    [Test]
    public void Delete_LastOffsetUnderAKey_KeyNoLongerAppearsInRange() {
        var offsets = new List<int>();
        var index = NewIndex();
        index.Insert(30, 0);

        index.Delete(30, 0);

        index.Range(1, 100, offsets);

        Assert.That(offsets, Is.Empty);
    }

    [Test]
    public void GetOffsets_ReturnsAllOffsetsForKey() {
        var offsets = new List<int>();
        var index = NewIndex();
        index.Insert(30, 0);
        index.Insert(30, 1);

        index.GetOffsets(30, offsets);
        
        Assert.That(offsets, Is.EquivalentTo(new[] { 0, 1 }));
    }

    [Test]
    public void GetOffsets_UnknownKey_ReturnsEmpty() {
        var offsets = new List<int>();
        var index = NewIndex();

        index.GetOffsets(30, offsets);
        
        Assert.That(offsets, Is.Empty);
    }

    [Test]
    public void Range_BoundaryKeysHaveMultipleEntries_AllOfThemIncluded() {
        var offsets = new List<int>();
        var index = NewIndex();
        index.Insert(20, 1);
        index.Insert(20, 2);
        index.Insert(30, 3);
        index.Insert(40, 4);
        index.Insert(40, 5);

        index.Range(20, 40, offsets);

        Assert.That(offsets, Is.EquivalentTo(new[] { 1, 2, 3, 4, 5 }));
    }

    [Test]
    public void Delete_ThenInsertAgainUnderSameKey_Succeeds() {
        var offsets = new List<int>();
        var index = NewIndex();
        index.Insert(30, 0);
        index.Delete(30, 0);

        index.Insert(30, 1);
        index.GetOffsets(30, offsets);

        Assert.That(offsets, Is.EqualTo(new[] { 1 }));
    }

    [Test]
    public void Insert_KeysOutOfOrder_MaintainsAscendingOrder() {
        var offsets = new List<int>();
        var index = NewIndex();
        var ages = new[] { 50, 10, 60, 30, 20, 40 }; // out of order on purpose
        for (var i = 0; i < ages.Length; i++)
            index.Insert(ages[i], i);

        index.Range(10, 60, offsets);

        Assert.That(offsets, Has.Count.EqualTo(6));
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
        var offsets = new List<int>();
        var index = NewScrambledIndex(601);

        index.Range(200, 300, offsets);

        Assert.That(offsets, Is.EqualTo(Enumerable.Range(200, 101).Select(k => k * 10)));
    }

    [Test]
    public void Range_PointQueryFarFromTheFirstChunk_ReturnsJustThatOffset() {
        var offsets = new List<int>();
        var index = NewScrambledIndex(601);

        index.Range(450, 450, offsets);

        Assert.That(offsets, Is.EqualTo(new[] { 4500 }));
    }

    [Test]
    public void Range_WhenFromFallsInAKeyGap_ReturnsOnlyTheKeysAboveIt() {
        var offsets = new List<int>();
        // Walk the whole permutation of 0..600 and keep only the even keys, so the
        // index holds exactly the even keys and every odd key is a gap.
        var index = NewIndex();
        for (var i = 0; i < 601; i++) {
            var key = (i * 677) % 601;
            if (key % 2 == 0) index.Insert(key, key * 10);
        }

        index.Range(399, 401, offsets);

        Assert.That(offsets, Is.EqualTo(new[] { 4000 }));
    }

    [Test]
    public void Range_FromBelowEveryKey_ReturnsTheWholeIndex() {
        var offsets = new List<int>();
        var index = NewScrambledIndex(601);

        index.Range(-1, 600, offsets);

        Assert.That(offsets, Is.EqualTo(Enumerable.Range(0, 601).Select(k => k * 10)));
    }

    // ---- Duplicate keys spanning more than one chunk ----

    [Test]
    public void Range_DuplicateRunFillingMoreThanOneChunk_ReturnsEveryOffset() {
        var offsets = new List<int>();
        var index = NewIndex();
        // More duplicates than a single chunk holds, so the run must span
        // at least one chunk boundary.
        for (var i = 0; i < 1000; i++)
            index.Insert(7, i);

        index.Range(7, 7, offsets);

        Assert.That(offsets, Has.Count.EqualTo(1000));
        Assert.That(offsets, Is.EquivalentTo(Enumerable.Range(0, 1000)));
    }

    [Test]
    public void Range_WhenFromEqualsAKeyWithLoadsOfDuplicates_IncludesEveryDuplicate() {
        var offsets = new List<int>();
        var index = NewIndex();
        for (var i = 0; i < 600; i++)
            index.Insert(7, i);
        index.Insert(8, 800);

        // 'from' is the duplicated key itself - the walk-back plus the forward
        // scan must pick the duplicates up across every chunk they landed in.
        index.Range(7, 8, offsets);

        Assert.That(offsets, Has.Count.EqualTo(601));
        Assert.That(offsets, Does.Contain(800));
    }

    [Test]
    public void Range_DuplicatesOnAKeyStraddlingAChunkBoundary_ReturnsOtherKeysToo() {
        var offsets = new List<int>();
        var index = NewIndex();
        for (var i = 0; i < 600; i++)
            index.Insert(7, 7000 + i);
        index.Insert(5, 500);
        index.Insert(9, 900);

        index.Range(0, 100, offsets);

        Assert.That(offsets, Has.Count.EqualTo(602));
        Assert.That(offsets, Does.Contain(500));
        Assert.That(offsets, Does.Contain(900));
        Assert.That(offsets.Take(2), Is.EqualTo(new[] { 500, 7000 }));
    }

    [Test]
    public void GetOffsets_ForKeyWithDuplicatesAcrossChunks_ReturnsEveryOffset() {
        var offsets = new List<int>();
        var index = NewIndex();
        for (var i = 0; i < 600; i++)
            index.Insert(7, i);

        index.GetOffsets(7, offsets);

        Assert.That(offsets, Has.Count.EqualTo(600));
        Assert.That(offsets, Is.EquivalentTo(Enumerable.Range(0, 600)));
    }
    [Test]
    public void Delete_OffsetInALaterChunkOfADuplicateRun_RemovesOnlyThatOffset() {
        var index = NewIndex();
        // 600 duplicates of one key: the run spans several chunks.
        for (var i = 0; i < 600; i++)
            index.Insert(7, i);
        index.Insert(8, 800);

        index.Delete(7, 599); // sits in a later chunk of the duplicate run

        var remaining = new List<int>();
        index.GetOffsets(7, remaining);
        Assert.That(remaining, Has.Count.EqualTo(599));
        Assert.That(remaining, Does.Not.Contain(599));
        Assert.That(remaining, Is.EquivalentTo(Enumerable.Range(0, 599)));

        var range = new List<int>();
        index.Range(7, 8, range);
        Assert.That(range, Does.Contain(800));
        Assert.That(range, Has.Count.EqualTo(600));
    }
    [Test]
    public void Range_AtScale_DuplicateRunsAcrossTensOfChunks_ReturnEveryOffset() {
        var index = NewIndex();
        // Keys 0..999 with ten duplicates each => ~40 chunks, so most duplicate
        // runs are not in the chunk the search starts at.
        for (var i = 0; i < 10_000; i++)
            index.Insert(i / 10, i);

        var offsets = new List<int>();
        index.Range(500, 500, offsets);
        Assert.That(offsets, Is.EquivalentTo(Enumerable.Range(5_000, 10)));

        offsets = new List<int>();
        index.Range(400, 499, offsets);
        Assert.That(offsets, Has.Count.EqualTo(1_000));
        Assert.That(offsets, Is.EquivalentTo(Enumerable.Range(4_000, 1_000)));

        offsets = new List<int>();
        index.GetOffsets(999, offsets);
        Assert.That(offsets, Is.EquivalentTo(Enumerable.Range(9_990, 10)));

        offsets = new List<int>();
        index.Range(0, 999, offsets);
        Assert.That(offsets, Has.Count.EqualTo(10_000));
    }
}

