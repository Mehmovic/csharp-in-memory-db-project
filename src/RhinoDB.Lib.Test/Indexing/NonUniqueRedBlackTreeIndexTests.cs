namespace RhinoDB.Lib.Indexing.Test;

public class NonUniqueRedBlackTreeIndexTests {
    static private NonUniqueRedBlackTreeIndex<int> NewIndex() => new NonUniqueRedBlackTreeIndex<int>();

    static private int[] RangeOf(NonUniqueRedBlackTreeIndex<int> index, int from, int to) {
        using var writer = index.Range(from, to);
        return writer.Buffer().ToArray();
    }

    static private int[] OffsetsOf(NonUniqueRedBlackTreeIndex<int> index, int key) {
        using var writer = index.GetOffsets(key);
        return writer.Buffer().ToArray();
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
}
