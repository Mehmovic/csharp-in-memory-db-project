using RhinoDB.Core.Exceptions;
using RhinoDB.Lib.Indexing;

namespace RhinoDB.Test.Lib.Indexing;

public class NonUniqueOrderedIndexTests {
    static private NonUniqueOrderedIndex<int> NewIndex() => new NonUniqueOrderedIndex<int>();

    [Test]
    public void Range_WithNoMatches_ReturnsEmpty() {
        var index = NewIndex();

        Assert.That(index.Range(1, 10), Is.Empty);
    }

    [Test]
    public void Insert_ThenRange_ReturnsTheInsertedOffset() {
        var index = NewIndex();

        index.Insert(30, 0);

        Assert.That(index.Range(30, 30), Is.EqualTo(new[] { 0 }));
    }

    [Test]
    public void Insert_MultipleOffsetsSameKey_RangeReturnsAllOfThem() {
        var index = NewIndex();

        index.Insert(30, 0);
        index.Insert(30, 1);

        Assert.That(index.Range(30, 30), Is.EquivalentTo(new[] { 0, 1 }));
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

        var offsets = index.Range(20, 35);

        Assert.That(offsets, Is.EqualTo(new[] { 1, 2, 3, 4 }));
    }

    [Test]
    public void Range_WhenFromIsGreaterThanTo_ReturnsEmpty() {
        var index = NewIndex();
        index.Insert(30, 0);

        Assert.That(index.Range(30, 1), Is.Empty);
    }

    [Test]
    public void Delete_RemovesOnlyTheGivenOffset_OtherOffsetsWithSameKeyRemain() {
        var index = NewIndex();
        index.Insert(30, 0);
        index.Insert(30, 1);

        index.Delete(30, 0);

        Assert.That(index.Range(30, 30), Is.EqualTo(new[] { 1 }));
    }

    [Test]
    public void Delete_LastOffsetUnderAKey_KeyNoLongerAppearsInRange() {
        var index = NewIndex();
        index.Insert(30, 0);

        index.Delete(30, 0);

        Assert.That(index.Range(1, 100), Is.Empty);
    }

    [Test]
    public void Delete_UnknownPair_ReturnsFailureWithOffsetNotRegisteredException() {
        var index = NewIndex();

        var res = index.Delete(30, 0);

        Assert.That(res.IsError(), Is.True);
        Assert.That(res.GetError().ToException(), Is.InstanceOf<OffsetNotRegisteredException>());
    }

    [Test]
    public void GetOffsets_ReturnsAllOffsetsForKey() {
        var index = NewIndex();
        index.Insert(30, 0);
        index.Insert(30, 1);

        Assert.That(index.GetOffsets(30), Is.EquivalentTo(new[] { 0, 1 }));
    }

    [Test]
    public void GetOffsets_UnknownKey_ReturnsEmpty() {
        var index = NewIndex();

        Assert.That(index.GetOffsets(30), Is.Empty);
    }

    [Test]
    public void Range_BoundaryKeysHaveMultipleEntries_AllOfThemIncluded() {
        var index = NewIndex();
        index.Insert(20, 1);
        index.Insert(20, 2);
        index.Insert(30, 3);
        index.Insert(40, 4);
        index.Insert(40, 5);

        var offsets = index.Range(20, 40);

        Assert.That(offsets, Is.EquivalentTo(new[] { 1, 2, 3, 4, 5 }));
    }

    [Test]
    public void Delete_ThenInsertAgainUnderSameKey_Succeeds() {
        var index = NewIndex();
        index.Insert(30, 0);
        index.Delete(30, 0);

        index.Insert(30, 1);

        Assert.That(index.GetOffsets(30), Is.EqualTo(new[] { 1 }));
    }

    [Test]
    public void Insert_KeysOutOfOrder_MaintainsAscendingOrder() {
        var index = NewIndex();
        var ages = new[] { 50, 10, 60, 30, 20, 40 }; // out of order on purpose
        for (var i = 0; i < ages.Length; i++)
            index.Insert(ages[i], i);

        var offsets = index.Range(10, 60);

        Assert.That(offsets.Count, Is.EqualTo(6));
    }
}
