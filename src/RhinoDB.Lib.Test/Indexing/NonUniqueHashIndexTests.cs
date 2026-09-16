namespace RhinoDB.Lib.Indexing.Test;

public class NonUniqueHashIndexTests {
    static private NonUniqueHashIndex<string> NewIndex() => new NonUniqueHashIndex<string>();

    [Test]
    public void GetOffsets_UnknownKey_ReturnsEmpty() {
        var offsets = new List<int>();                
        var index = NewIndex();
        index.GetOffsets("Red", offsets);

        Assert.That(offsets, Is.Empty);
    }

    [Test]
    public void Insert_ThenGetOffsets_ReturnsTheInsertedOffset() {
        var offsets = new List<int>();
        var index = NewIndex();

        index.Insert("Red", 0);

        index.GetOffsets("Red", offsets);
        
        Assert.That(offsets, Is.EqualTo(new[] { 0 }));
    }

    [Test]
    public void Insert_MultipleOffsetsSameKey_GetOffsetsReturnsAllOfThem() {
        var offsets = new List<int>();
        var index = NewIndex();

        index.Insert("Red", 0);
        index.Insert("Red", 1);
        index.Insert("Blue", 2);

        index.GetOffsets("Red", offsets);
        Assert.That(offsets, Is.EquivalentTo(new[] { 0, 1 }));

        offsets.Clear();
        index.GetOffsets("Blue", offsets);
        Assert.That(offsets, Is.EquivalentTo(new[] { 2 }));
    }

    [Test]
    public void Insert_SameOffsetTwiceUnderSameKey_IsIdempotent() {
        var offsets = new List<int>();
        // Divergence from the List-backed NonUniqueHashIndex: a HashSet bucket
        // collapses a re-inserted offset instead of adding a duplicate entry.
        var index = NewIndex();

        index.Insert("Red", 0);
        index.Insert("Red", 0);

        index.GetOffsets("Red", offsets);
        
        Assert.That(offsets, Is.EqualTo(new[] { 0 }));
    }

    [Test]
    public void Delete_RemovesOnlyTheGivenOffset_OtherOffsetsWithSameKeyRemain() {
        var offsets = new List<int>();        
        var index = NewIndex();
        index.Insert("Red", 0);
        index.Insert("Red", 1);
        index.Insert("Red", 2);

        index.Delete("Red", 1);

        index.GetOffsets("Red", offsets);
        
        Assert.That(offsets, Is.EquivalentTo(new[] { 0, 2 }));
    }

    [Test]
    public void Delete_LastOffsetUnderAKey_KeyNoLongerAppearsInGetOffsets() {
        var offsets = new List<int>();
        var index = NewIndex();
        index.Insert("Red", 0);

        index.Delete("Red", 0);

        index.GetOffsets("Red", offsets);

        Assert.That(offsets, Is.Empty);
    }

    [Test]
    public void Delete_ThenInsertAgainUnderSameKey_RecreatesTheBucket() {
        var offsets = new List<int>();
        var index = NewIndex();
        index.Insert("Red", 0);
        index.Delete("Red", 0); // bucket is now removed entirely

        index.Insert("Red", 1);

        index.GetOffsets("Red", offsets);
        
        Assert.That(offsets, Is.EqualTo(new[] { 1 }));
    }
}
