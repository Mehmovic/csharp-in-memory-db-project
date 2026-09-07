namespace RhinoDB.Lib.Indexing.Test;

public class NonUniqueHashIndexTests {
    static private NonUniqueHashIndex<string> NewIndex() => new NonUniqueHashIndex<string>();

    [Test]
    public void GetOffsets_UnknownKey_ReturnsEmpty() {
        var index = NewIndex();

        Assert.That(index.GetOffsets("Red"), Is.Empty);
    }

    [Test]
    public void Insert_ThenGetOffsets_ReturnsTheInsertedOffset() {
        var index = NewIndex();

        index.Insert("Red", 0);

        Assert.That(index.GetOffsets("Red"), Is.EqualTo(new[] { 0 }));
    }

    [Test]
    public void Insert_MultipleOffsetsSameKey_GetOffsetsReturnsAllOfThem() {
        var index = NewIndex();

        index.Insert("Red", 0);
        index.Insert("Red", 1);
        index.Insert("Blue", 2);

        Assert.That(index.GetOffsets("Red"), Is.EquivalentTo(new[] { 0, 1 }));
        Assert.That(index.GetOffsets("Blue"), Is.EquivalentTo(new[] { 2 }));
    }

    [Test]
    public void Delete_RemovesOnlyTheGivenOffset_OtherOffsetsWithSameKeyRemain() {
        // Three offsets under one key exercises the swap-pop path in Delete
        // (removing the middle entry swaps the last one into its place).
        var index = NewIndex();
        index.Insert("Red", 0);
        index.Insert("Red", 1);
        index.Insert("Red", 2);

        index.Delete("Red", 1);

        Assert.That(index.GetOffsets("Red"), Is.EquivalentTo(new[] { 0, 2 }));
    }

    [Test]
    public void Delete_LastOffsetUnderAKey_KeyNoLongerAppearsInGetOffsets() {
        var index = NewIndex();
        index.Insert("Red", 0);

        index.Delete("Red", 0);

        Assert.That(index.GetOffsets("Red"), Is.Empty);
    }

    [Test]
    public void Delete_FirstOfTwo_RemainingOffsetStillRetrievable() {
        var index = NewIndex();
        index.Insert("Red", 0);
        index.Insert("Red", 1);

        index.Delete("Red", 0); // first entry - swaps the last element into its place

        Assert.That(index.GetOffsets("Red"), Is.EqualTo(new[] { 1 }));
    }

    [Test]
    public void Delete_LastPositionedOfMultiple_LeavesOthersIntact() {
        // Removing the physically-last entry in the bucket is the self-swap edge case
        // (position == lastIndex): offsets[position] = offsets[lastIndex] is a no-op
        // before RemoveAt, and must not corrupt the remaining entries.
        var index = NewIndex();
        index.Insert("Red", 0);
        index.Insert("Red", 1);
        index.Insert("Red", 2);

        index.Delete("Red", 2); // last inserted - lands at the bucket's last position

        Assert.That(index.GetOffsets("Red"), Is.EquivalentTo(new[] { 0, 1 }));
    }

    [Test]
    public void Delete_ThenInsertAgainUnderSameKey_RecreatesTheBucket() {
        var index = NewIndex();
        index.Insert("Red", 0);
        index.Delete("Red", 0); // bucket is now removed entirely

        index.Insert("Red", 1);

        Assert.That(index.GetOffsets("Red"), Is.EqualTo(new[] { 1 }));
    }
}
