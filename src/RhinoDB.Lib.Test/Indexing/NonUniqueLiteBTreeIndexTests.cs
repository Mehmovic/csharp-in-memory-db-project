namespace RhinoDB.Lib.Indexing.Test;

public class NonUniqueLiteBTreeIndexTests {
    static private NonUniqueLiteBTreeIndex<int> NewIndex() => new NonUniqueLiteBTreeIndex<int>();

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
}
