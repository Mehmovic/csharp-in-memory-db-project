namespace RhinoDB.Lib.Indexing.Test;

// Composite keys aren't a distinct feature of any index type - they fall out of TKey
// being generic and ValueTuple's built-in equality/comparison. These tests exist to
// lock in that (int X, int Y) behaves correctly as TKey across all four index types:
// hash equality must consider the whole tuple, and ordering must be X-then-Y
// (leftmost-prefix), not just X.

public class HashIndex_CompositeKeyTests {
    static private HashIndex<(int X, int Y)> NewIndex() => new HashIndex<(int X, int Y)>();

    [Test]
    public void Insert_DifferentXSameY_BothRetrievable() {
        var index = NewIndex();

        index.Insert((5, 10), 0);
        index.Insert((6, 10), 1);

        Assert.That(index.GetOffset((5, 10)).Unwrap(), Is.EqualTo(0));
        Assert.That(index.GetOffset((6, 10)).Unwrap(), Is.EqualTo(1));
    }

    [Test]
    public void Insert_SameXDifferentY_BothRetrievable() {
        var index = NewIndex();

        index.Insert((5, 10), 0);
        index.Insert((5, 20), 1);

        Assert.That(index.GetOffset((5, 10)).Unwrap(), Is.EqualTo(0));
        Assert.That(index.GetOffset((5, 20)).Unwrap(), Is.EqualTo(1));
    }

    [Test]
    public void GetOffset_ByFullCompositeKey_ReturnsTheMatchingOffset() {
        var index = NewIndex();
        index.Insert((5, 10), 0);

        Assert.That(index.GetOffset((5, 10)).Unwrap(), Is.EqualTo(0));
    }

    [Test]
    public void GetOffset_PartialMatchOnlyX_ReturnsFailure() {
        var index = NewIndex();
        index.Insert((5, 10), 0);

        Assert.That(index.GetOffset((5, 999)).IsError(), Is.True);
    }
}

public class OrderedIndex_CompositeKeyTests {
    static private RedBlackTreeIndex<(int X, int Y)> NewIndex() => new RedBlackTreeIndex<(int X, int Y)>();

    static private int[] RangeOf(RedBlackTreeIndex<(int X, int Y)> index, (int X, int Y) from, (int X, int Y) to) {
        using var writer = index.Range(from, to);
        return writer.Buffer().Unwrap().ToArray();
    }

    [Test]
    public void Insert_SameXDifferentY_BothRetrievable() {
        var index = NewIndex();

        index.Insert((5, 10), 0);
        index.Insert((5, 20), 1);

        Assert.That(index.GetOffset((5, 10)).Unwrap(), Is.EqualTo(0));
        Assert.That(index.GetOffset((5, 20)).Unwrap(), Is.EqualTo(1));
    }

    [Test]
    public void Range_OrdersByXThenY() {
        var index = NewIndex();
        // Inserted out of order on purpose - ordering must come from the index.
        index.Insert((2, 5), 1);
        index.Insert((1, 9), 2);
        index.Insert((1, 1), 3);
        index.Insert((2, 1), 4);

        Assert.That(RangeOf(index, (1, 0), (2, 9)), Is.EqualTo(new[] { 3, 2, 4, 1 }));
    }

    [Test]
    public void Range_FixedXRangeOnY_LeftmostPrefixQuery() {
        // The common composite-index access pattern: exact match on the leading
        // column, range on the trailing one.
        var index = NewIndex();
        index.Insert((5, 10), 1);
        index.Insert((5, 20), 2);
        index.Insert((5, 30), 3);
        index.Insert((6, 15), 4); // different X - must not appear

        Assert.That(RangeOf(index, (5, 10), (5, 20)), Is.EqualTo(new[] { 1, 2 }));
    }
}

public class NonUniqueHashSetIndex_CompositeKeyTests {
    static private NonUniqueHashIndex<(int X, int Y)> NewIndex() => new NonUniqueHashIndex<(int X, int Y)>();

    [Test]
    public void Insert_MultipleEntitiesAtSameCoordinate_GetOffsetsReturnsAllOfThem() {
        var offsets = new List<int>();
        var index = NewIndex();
        index.Insert((5, 10), 1);
        index.Insert((5, 10), 2);
        index.GetOffsets((5, 10), offsets);

        Assert.That(offsets, Is.EquivalentTo(new[] { 1, 2 }));
    }

    [Test]
    public void GetOffsets_PartialMatchOnlyX_ReturnsEmpty() {
        var offsets = new List<int>();
        var index = NewIndex();
        index.Insert((5, 10), 1);
        index.GetOffsets((5, 999), offsets);

        Assert.That(offsets, Is.Empty);
    }

    [Test]
    public void Insert_SameXDifferentY_TreatedAsDistinctKeys() {
        var offsets = new List<int>();
        var index = NewIndex();
        index.Insert((5, 10), 1);
        index.Insert((5, 20), 2);

        index.GetOffsets((5, 10), offsets);
        Assert.That(offsets, Is.EqualTo(new[] { 1 }));
        
        offsets.Clear();
        index.GetOffsets((5, 20), offsets);

        Assert.That(offsets, Is.EqualTo(new[] { 2 }));
    }
}

public class NonUniqueOrderedIndex_CompositeKeyTests {
    static private NonUniqueRedBlackTreeIndex<(int X, int Y)> NewIndex() => new NonUniqueRedBlackTreeIndex<(int X, int Y)>();

    static private int[] RangeOf(NonUniqueRedBlackTreeIndex<(int X, int Y)> index, (int X, int Y) from, (int X, int Y) to) {
        using var writer = index.Range(from, to);
        return writer.Buffer().Unwrap().ToArray();
    }

    static private int[] OffsetsOf(NonUniqueRedBlackTreeIndex<(int X, int Y)> index, (int X, int Y) key) {
        using var writer = index.GetOffsets(key);
        return writer.Buffer().Unwrap().ToArray();
    }

    [Test]
    public void Insert_MultipleEntitiesAtSameCoordinate_GetOffsetsReturnsAllOfThem() {
        var index = NewIndex();
        index.Insert((5, 10), 1);
        index.Insert((5, 10), 2);

        Assert.That(OffsetsOf(index, (5, 10)), Is.EquivalentTo(new[] { 1, 2 }));
    }

    [Test]
    public void Range_OrdersByXThenY_AcrossDuplicateCoordinates() {
        var index = NewIndex();
        index.Insert((2, 5), 1);
        index.Insert((1, 9), 2);
        index.Insert((1, 1), 3);
        index.Insert((1, 1), 4); // same (X,Y) as offset 3 - must coexist

        Assert.That(RangeOf(index, (1, 0), (2, 9)), Is.EquivalentTo(new[] { 3, 4, 2, 1 }));
    }

    [Test]
    public void Range_FixedXRangeOnY_LeftmostPrefixQuery() {
        var index = NewIndex();
        index.Insert((5, 10), 1);
        index.Insert((5, 20), 2);
        index.Insert((6, 15), 3); // different X - must not appear

        Assert.That(RangeOf(index, (5, 10), (5, 20)), Is.EquivalentTo(new[] { 1, 2 }));
    }

    [Test]
    public void Range_DoesNotComposeAsIndependentBoundingBox() {
        // Documents the caveat: Range on a composite key is lexicographic, not a
        // rectangle. An offset can fall inside the bound even when one of its columns
        // is far outside what looks like that column's range, because the other
        // column's ordering "carries" it into the bound.
        var index = NewIndex();
        var withinIntendedBox = 1;
        var lexicographicallyBetweenButYOutOfRange = 2;
        index.Insert((1, 5), withinIntendedBox);
        index.Insert((1, 50), lexicographicallyBetweenButYOutOfRange);

        // Both come back, even though entry 2's Y (50) is well outside the [0,9]
        // bound someone might have intended as an independent Y-axis limit.
        Assert.That(RangeOf(index, (1, 0), (2, 9)), Is.EquivalentTo(new[] { 1, 2 }));
    }
}
