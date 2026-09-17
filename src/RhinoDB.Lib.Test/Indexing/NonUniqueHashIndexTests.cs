namespace RhinoDB.Lib.Indexing.Test;

public class NonUniqueHashIndexTests {
    static private NonUniqueHashIndex<string> NewIndex() => new NonUniqueHashIndex<string>();

    // GetOffsets returns a pooled, disposable OffsetList; materialize to a plain
    // array so assertions stay readable and the rental goes back fast.
    static private int[] OffsetsOf(NonUniqueHashIndex<string> index, string key) {
        using var list = index.GetOffsets(key);
        return list.Buffer().ToArray();
    }

    [Test]
    public void GetOffsets_UnknownKey_ReturnsEmpty() {
        var index = NewIndex();

        Assert.That(OffsetsOf(index, "Red"), Is.Empty);
    }

    [Test]
    public void Insert_ThenGetOffsets_ReturnsTheInsertedOffset() {
        var index = NewIndex();

        index.Insert("Red", 0);

        Assert.That(OffsetsOf(index, "Red"), Is.EqualTo(new[] { 0 }));
    }

    [Test]
    public void Insert_MultipleOffsetsSameKey_GetOffsetsReturnsAllOfThem() {
        var index = NewIndex();

        index.Insert("Red", 0);
        index.Insert("Red", 1);
        index.Insert("Blue", 2);

        Assert.That(OffsetsOf(index, "Red"), Is.EquivalentTo(new[] { 0, 1 }));
        Assert.That(OffsetsOf(index, "Blue"), Is.EquivalentTo(new[] { 2 }));
    }

    [Test]
    public void Insert_SameOffsetTwiceUnderSameKey_IsIdempotent() {
        // Divergence from the List-backed NonUniqueHashIndex: a HashSet bucket
        // collapses a re-inserted offset instead of adding a duplicate entry.
        var index = NewIndex();

        index.Insert("Red", 0);
        index.Insert("Red", 0);

        Assert.That(OffsetsOf(index, "Red"), Is.EqualTo(new[] { 0 }));
    }

    [Test]
    public void Delete_RemovesOnlyTheGivenOffset_OtherOffsetsWithSameKeyRemain() {
        var index = NewIndex();
        index.Insert("Red", 0);
        index.Insert("Red", 1);
        index.Insert("Red", 2);

        index.Delete("Red", 1);

        Assert.That(OffsetsOf(index, "Red"), Is.EquivalentTo(new[] { 0, 2 }));
    }

    [Test]
    public void Delete_LastOffsetUnderAKey_KeyNoLongerAppearsInGetOffsets() {
        var index = NewIndex();
        index.Insert("Red", 0);

        index.Delete("Red", 0);

        Assert.That(OffsetsOf(index, "Red"), Is.Empty);
    }

    [Test]
    public void Delete_ThenInsertAgainUnderSameKey_RecreatesTheBucket() {
        var index = NewIndex();
        index.Insert("Red", 0);
        index.Delete("Red", 0); // bucket is now removed entirely

        index.Insert("Red", 1);

        Assert.That(OffsetsOf(index, "Red"), Is.EqualTo(new[] { 1 }));
    }
}
