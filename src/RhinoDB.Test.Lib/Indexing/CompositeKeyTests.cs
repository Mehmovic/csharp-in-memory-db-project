using RhinoDB.Core.Exceptions;
using RhinoDB.Lib.Indexing;
using RhinoDB.Lib.Storage;

namespace RhinoDB.Test.Lib.Indexing;

// Composite keys aren't a distinct feature of any index type - they fall out of TKey
// being generic and ValueTuple's built-in equality/comparison. These tests exist to
// lock in that (int X, int Y) behaves correctly as TKey across all four index types:
// hash equality must consider the whole tuple, and ordering must be X-then-Y
// (leftmost-prefix), not just X.

public class HashIndex_CompositeKeyTests
{
    private readonly record struct Position(int EntityId, int X, int Y);

    static private HashIndex<(int X, int Y), Position> NewIndex() => new HashIndex<(int X, int Y), Position>(
        new DenseArray<Position>(chunkSize: 4), row => (row.X, row.Y));

    [Test]
    public void Insert_DifferentXSameY_BothSucceed()
    {
        var index = NewIndex();

        var a = index.Insert(new Position(1, 5, 10));
        var b = index.Insert(new Position(2, 6, 10));

        Assert.That(a.IsOk(), Is.True);
        Assert.That(b.IsOk(), Is.True);
    }

    [Test]
    public void Insert_SameXDifferentY_BothSucceed()
    {
        var index = NewIndex();

        var a = index.Insert(new Position(1, 5, 10));
        var b = index.Insert(new Position(2, 5, 20));

        Assert.That(a.IsOk(), Is.True);
        Assert.That(b.IsOk(), Is.True);
    }

    [Test]
    public void Insert_SameXAndY_RejectedAsDuplicate()
    {
        var index = NewIndex();
        index.Insert(new Position(1, 5, 10));

        var result = index.Insert(new Position(2, 5, 10));

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetException(), Is.InstanceOf<DuplicateKeyException>());
    }

    [Test]
    public void Get_ByFullCompositeKey_ReturnsTheMatchingRow()
    {
        var index = NewIndex();
        index.Insert(new Position(1, 5, 10));

        Assert.That(index.Get((5, 10)).Unwrap(), Is.EqualTo(new Position(1, 5, 10)));
    }

    [Test]
    public void Get_PartialMatchOnlyX_ReturnsFailure()
    {
        var index = NewIndex();
        index.Insert(new Position(1, 5, 10));

        Assert.That(index.Get((5, 999)).IsError(), Is.True);
    }
}

public class OrderedIndex_CompositeKeyTests
{
    private readonly record struct Position(int EntityId, int X, int Y);

    static private OrderedIndex<(int X, int Y), Position> NewIndex() => new OrderedIndex<(int X, int Y), Position>(
        new DenseArray<Position>(chunkSize: 4), row => (row.X, row.Y));

    [Test]
    public void Insert_SameXAndY_RejectedAsDuplicate()
    {
        var index = NewIndex();
        index.Insert(new Position(1, 5, 10));

        var result = index.Insert(new Position(2, 5, 10));

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetException(), Is.InstanceOf<DuplicateKeyException>());
    }

    [Test]
    public void Insert_SameXDifferentY_BothSucceed()
    {
        var index = NewIndex();

        var a = index.Insert(new Position(1, 5, 10));
        var b = index.Insert(new Position(2, 5, 20));

        Assert.That(a.IsOk(), Is.True);
        Assert.That(b.IsOk(), Is.True);
    }

    [Test]
    public void Range_OrdersByXThenY()
    {
        var index = NewIndex();
        // Inserted out of order on purpose - ordering must come from the index.
        index.Insert(new Position(1, 2, 5));
        index.Insert(new Position(2, 1, 9));
        index.Insert(new Position(3, 1, 1));
        index.Insert(new Position(4, 2, 1));

        var rows = index.Range((1, 0), (2, 9));

        Assert.That(rows.Select(r => (r.X, r.Y)), Is.EqualTo(new[] { (1, 1), (1, 9), (2, 1), (2, 5) }));
    }

    [Test]
    public void Range_FixedXRangeOnY_LeftmostPrefixQuery()
    {
        // The common composite-index access pattern: exact match on the leading
        // column, range on the trailing one.
        var index = NewIndex();
        index.Insert(new Position(1, 5, 10));
        index.Insert(new Position(2, 5, 20));
        index.Insert(new Position(3, 5, 30));
        index.Insert(new Position(4, 6, 15)); // different X - must not appear

        var rows = index.Range((5, 10), (5, 20));

        Assert.That(rows.Select(r => r.EntityId), Is.EqualTo(new[] { 1, 2 }));
    }
}

public class NonUniqueHashIndex_CompositeKeyTests
{
    private readonly record struct Position(int EntityId, int X, int Y);

    static private (DenseArray<Position> Storage, NonUniqueHashIndex<(int X, int Y), Position> Index) NewIndex() {
        var storage = new DenseArray<Position>(chunkSize: 4);
        return (storage, new NonUniqueHashIndex<(int X, int Y), Position>(storage));
    }

    [Test]
    public void Register_MultipleEntitiesAtSameCoordinate_GetReturnsAllOfThem()
    {
        var (storage, index) = NewIndex();
        var a = storage.Insert(new Position(1, 5, 10));
        var b = storage.Insert(new Position(2, 5, 10));
        index.Register((5, 10), a);
        index.Register((5, 10), b);

        Assert.That(index.Get((5, 10)), Is.EquivalentTo(new[] { new Position(1, 5, 10), new Position(2, 5, 10) }));
    }

    [Test]
    public void Get_PartialMatchOnlyX_ReturnsEmpty()
    {
        var (storage, index) = NewIndex();
        var offset = storage.Insert(new Position(1, 5, 10));
        index.Register((5, 10), offset);

        Assert.That(index.Get((5, 999)), Is.Empty);
    }

    [Test]
    public void Register_SameXDifferentY_TreatedAsDistinctKeys()
    {
        var (storage, index) = NewIndex();
        var a = storage.Insert(new Position(1, 5, 10));
        var b = storage.Insert(new Position(2, 5, 20));
        index.Register((5, 10), a);
        index.Register((5, 20), b);

        Assert.That(index.Get((5, 10)), Is.EqualTo(new[] { new Position(1, 5, 10) }));
        Assert.That(index.Get((5, 20)), Is.EqualTo(new[] { new Position(2, 5, 20) }));
    }
}

public class NonUniqueOrderedIndex_CompositeKeyTests
{
    private readonly record struct Position(int EntityId, int X, int Y);

    static private (DenseArray<Position> Storage, NonUniqueOrderedIndex<(int X, int Y), Position> Index) NewIndex() {
        var storage = new DenseArray<Position>(chunkSize: 4);
        return (storage, new NonUniqueOrderedIndex<(int X, int Y), Position>(storage));
    }

    [Test]
    public void Register_MultipleEntitiesAtSameCoordinate_GetReturnsAllOfThem()
    {
        var (storage, index) = NewIndex();
        var a = storage.Insert(new Position(1, 5, 10));
        var b = storage.Insert(new Position(2, 5, 10));
        index.Register((5, 10), a);
        index.Register((5, 10), b);

        Assert.That(index.Get((5, 10)), Is.EquivalentTo(new[] { new Position(1, 5, 10), new Position(2, 5, 10) }));
    }

    [Test]
    public void Range_OrdersByXThenY_AcrossDuplicateCoordinates()
    {
        var (storage, index) = NewIndex();
        var a = storage.Insert(new Position(1, 2, 5));
        var b = storage.Insert(new Position(2, 1, 9));
        var c = storage.Insert(new Position(3, 1, 1));
        var d = storage.Insert(new Position(4, 1, 1)); // same (X,Y) as c - must coexist
        index.Register((2, 5), a);
        index.Register((1, 9), b);
        index.Register((1, 1), c);
        index.Register((1, 1), d);

        var rows = index.Range((1, 0), (2, 9));

        Assert.That(rows.Select(r => (r.X, r.Y)), Is.EqualTo(new[] { (1, 1), (1, 1), (1, 9), (2, 5) }));
    }

    [Test]
    public void Range_FixedXRangeOnY_LeftmostPrefixQuery()
    {
        var (storage, index) = NewIndex();
        var a = storage.Insert(new Position(1, 5, 10));
        var b = storage.Insert(new Position(2, 5, 20));
        var c = storage.Insert(new Position(3, 6, 15)); // different X - must not appear
        index.Register((5, 10), a);
        index.Register((5, 20), b);
        index.Register((6, 15), c);

        var rows = index.Range((5, 10), (5, 20));

        Assert.That(rows.Select(r => r.EntityId), Is.EquivalentTo(new[] { 1, 2 }));
    }

    [Test]
    public void Range_DoesNotComposeAsIndependentBoundingBox()
    {
        // Documents the caveat: Range on a composite key is lexicographic, not a
        // rectangle. A row can fall inside the bound even when one of its columns
        // is far outside what looks like that column's range, because the other
        // column's ordering "carries" it into the bound.
        var (storage, index) = NewIndex();
        var withinIntendedBox = storage.Insert(new Position(1, 1, 5));
        var lexicographicallyBetweenButYOutOfRange = storage.Insert(new Position(2, 1, 50));
        index.Register((1, 5), withinIntendedBox);
        index.Register((1, 50), lexicographicallyBetweenButYOutOfRange);

        var rows = index.Range((1, 0), (2, 9));

        // Both rows come back, even though entity 2's Y (50) is well outside the
        // [0,9] bound someone might have intended as an independent Y-axis limit.
        Assert.That(rows.Select(r => r.EntityId), Is.EquivalentTo(new[] { 1, 2 }));
    }
}
