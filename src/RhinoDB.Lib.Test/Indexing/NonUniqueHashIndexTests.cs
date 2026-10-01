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

// ---- Scan surface: GetOffsetsIter / GetOffsetsExcept / ScanOffsets ----
// The Insert/GetOffsets/Delete cases above all go through GetOffsets, which reads one HashSet bucket.
// Scanning is the other half of the class - the path the generated table uses for Iter() and Except() -
// and it was untested. It has three distinct branches: an empty map, a filtered scan, and an
// unfiltered one.

    static private int[] IterOffsetsOf(NonUniqueHashIndex<string> index) {
        using var list = index.GetOffsetsIter();
        return list.Buffer().ToArray();
    }

    static private int[] ExceptOffsetsOf(NonUniqueHashIndex<string> index, string excludedKey) {
        using var list = index.GetOffsetsExcept(excludedKey);
        return list.Buffer().ToArray();
    }

    [Test]
    public void GetOffsetsIter_OnAnEmptyIndex_ReturnsEmpty() {
        var index = NewIndex();

        Assert.That(IterOffsetsOf(index), Is.Empty);
    }

    [Test]
    public void GetOffsetsIter_ReturnsEveryOffsetAcrossEveryKey() {
        var index = NewIndex();
        index.Insert("Red", 0);
        index.Insert("Red", 1);
        index.Insert("Blue", 2);
        index.Insert("Green", 3);

        Assert.That(IterOffsetsOf(index), Is.EquivalentTo(new[] { 0, 1, 2, 3 }));
    }

    [Test]
    public void GetOffsetsIter_AfterEveryOffsetIsDeleted_ReturnsEmpty() {
        var index = NewIndex();
        index.Insert("Red", 0);
        index.Insert("Blue", 1);

        index.Delete("Red", 0);   // drops the last offset under "Red", so the bucket is removed
        index.Delete("Blue", 1);

        Assert.That(index.GetOffsetsIter().Count, Is.EqualTo(0),
            "Delete removes a bucket once it empties, so the map must be empty again - not left with an empty set.");
    }

    [Test]
    public void GetOffsetsExcept_SkipsTheExcludedKeyAndKeepsTheRest() {
        var index = NewIndex();
        index.Insert("Red", 0);
        index.Insert("Red", 1);
        index.Insert("Blue", 2);

        Assert.That(ExceptOffsetsOf(index, "Red"), Is.EqualTo(new[] { 2 }));
    }

    [Test]
    public void GetOffsetsExcept_WhenTheExcludedKeyIsTheOnlyOne_ReturnsEmpty() {
        var index = NewIndex();
        index.Insert("Red", 0);

        Assert.That(ExceptOffsetsOf(index, "Red"), Is.Empty);
    }

    [Test]
    public void GetOffsetsExcept_WithAnUnknownKey_ReturnsEveryOffset() {
        var index = NewIndex();
        index.Insert("Red", 0);
        index.Insert("Blue", 1);

        Assert.That(ExceptOffsetsOf(index, "Gold"), Is.EquivalentTo(new[] { 0, 1 }),
            "Excluding a key that was never inserted must not drop anything.");
    }

    [Test]
    public void Scanning_GrowsPastTheInitialBuilderCapacityAndKeepsEveryOffset() {
        var index = NewIndex();
        const int count = 500;
        for (var i = 0; i < count; i++) index.Insert("key" + (i % 7), i);

        Assert.That(IterOffsetsOf(index), Is.EquivalentTo(Enumerable.Range(0, count).ToArray()),
            "The scan builds through one pooled buffer that grows; a growth bug would drop or repeat offsets.");
    }
}
