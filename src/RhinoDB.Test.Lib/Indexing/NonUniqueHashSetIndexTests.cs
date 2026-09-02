using RhinoDB.Core.Exceptions;
using RhinoDB.Lib.Indexing;

namespace RhinoDB.Test.Lib.Indexing;

public class NonUniqueHashSetIndexTests {
    static private NonUniqueHashSetIndex<string> NewIndex() => new NonUniqueHashSetIndex<string>();

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
    public void Insert_SameOffsetTwiceUnderSameKey_IsIdempotent() {
        // Divergence from the List-backed NonUniqueHashIndex: a HashSet bucket
        // collapses a re-inserted offset instead of adding a duplicate entry.
        var index = NewIndex();

        index.Insert("Red", 0);
        index.Insert("Red", 0);

        Assert.That(index.GetOffsets("Red"), Is.EqualTo(new[] { 0 }));
    }

    [Test]
    public void Delete_RemovesOnlyTheGivenOffset_OtherOffsetsWithSameKeyRemain() {
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
    public void Delete_UnknownKey_ReturnsFailureWithIndexKeyNotFoundException() {
        var index = NewIndex();

        var res = index.Delete("Red", 0);

        Assert.That(res.IsError(), Is.True);
        Assert.That(res.GetError().ToException(), Is.InstanceOf<IndexKeyNotFoundException>());
    }

    [Test]
    public void Delete_OffsetNotUnderTheGivenKey_ReturnsFailureWithOffsetNotRegisteredException() {
        var index = NewIndex();
        index.Insert("Red", 0);

        var res = index.Delete("Red", 999);

        Assert.That(res.IsError(), Is.True);
        Assert.That(res.GetError().ToException(), Is.InstanceOf<OffsetNotRegisteredException>());
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
