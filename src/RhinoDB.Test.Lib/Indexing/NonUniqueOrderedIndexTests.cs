using RhinoDB.Core.Exceptions;
using RhinoDB.Lib.Indexing;
using RhinoDB.Lib.Storage;

namespace RhinoDB.Test.Lib.Indexing;

public class NonUniqueOrderedIndexTests {
    private readonly record struct TestRow(int Id, int Age);

    static private (DenseArray<TestRow> Storage, NonUniqueOrderedIndex<int, TestRow> Index) NewIndex() {
        var storage = new DenseArray<TestRow>(chunkSize: 4);
        return (storage, new NonUniqueOrderedIndex<int, TestRow>(storage));
    }

    [Test]
    public void Range_WithNoMatches_ReturnsEmpty() {
        var (_, index) = NewIndex();

        Assert.That(index.Range(1, 10), Is.Empty);
    }

    [Test]
    public void Register_ThenRange_ReturnsTheRegisteredRow() {
        var (storage, index) = NewIndex();
        var offset = storage.Insert(new TestRow(1, 30));

        index.Register(30, offset);

        Assert.That(index.Range(30, 30), Is.EqualTo(new[] { new TestRow(1, 30) }));
    }

    [Test]
    public void Register_MultipleRowsSameKey_RangeReturnsAllOfThem() {
        var (storage, index) = NewIndex();
        var a = storage.Insert(new TestRow(1, 30));
        var b = storage.Insert(new TestRow(2, 30));

        index.Register(30, a);
        index.Register(30, b);

        Assert.That(index.Range(30, 30), Is.EquivalentTo(new[] { new TestRow(1, 30), new TestRow(2, 30) }));
    }

    [Test]
    public void Range_ReturnsRowsAcrossMultipleKeysInAscendingKeyOrder_DuplicatesGroupedByKey() {
        var (storage, index) = NewIndex();
        // Inserted out of key order on purpose - ordering must come from the index
        // itself, not from insertion order or physical array position.
        var eve = storage.Insert(new TestRow(5, 40));
        var alice = storage.Insert(new TestRow(1, 20));
        var bob1 = storage.Insert(new TestRow(2, 25));
        var bob2 = storage.Insert(new TestRow(3, 25));
        var dave = storage.Insert(new TestRow(4, 35));

        index.Register(40, eve);
        index.Register(20, alice);
        index.Register(25, bob1);
        index.Register(25, bob2);
        index.Register(35, dave);

        var rows = index.Range(20, 35);

        Assert.That(rows.Select(r => r.Age), Is.EqualTo(new[] { 20, 25, 25, 35 }));
    }

    [Test]
    public void Range_WhenFromIsGreaterThanTo_ReturnsEmpty() {
        var (storage, index) = NewIndex();
        var offset = storage.Insert(new TestRow(1, 30));
        index.Register(30, offset);

        Assert.That(index.Range(30, 1), Is.Empty);
    }

    [Test]
    public void Deregister_RemovesOnlyTheGivenOffset_OtherRowsWithSameKeyRemain() {
        var (storage, index) = NewIndex();
        var a = storage.Insert(new TestRow(1, 30));
        var b = storage.Insert(new TestRow(2, 30));
        index.Register(30, a);
        index.Register(30, b);

        index.Deregister(30, a);

        Assert.That(index.Range(30, 30), Is.EqualTo(new[] { new TestRow(2, 30) }));
    }

    [Test]
    public void Deregister_LastOffsetUnderAKey_KeyNoLongerAppearsInRange() {
        var (storage, index) = NewIndex();
        var offset = storage.Insert(new TestRow(1, 30));
        index.Register(30, offset);

        index.Deregister(30, offset);

        Assert.That(index.Range(1, 100), Is.Empty);
    }

    [Test]
    public void Deregister_UnknownPair_ReturnsFailureWithOffsetNotRegisteredException() {
        var (_, index) = NewIndex();

        var res = index.Deregister(30, 0);

        Assert.That(res.IsError(), Is.True);
        Assert.That(res.GetException(), Is.InstanceOf<OffsetNotRegisteredException>());
    }

    [Test]
    public void Get_ReturnsAllRowsForKey() {
        var (storage, index) = NewIndex();
        var a = storage.Insert(new TestRow(1, 30));
        var b = storage.Insert(new TestRow(2, 30));
        index.Register(30, a);
        index.Register(30, b);

        Assert.That(index.Get(30), Is.EquivalentTo(new[] { new TestRow(1, 30), new TestRow(2, 30) }));
    }

    [Test]
    public void Get_UnknownKey_ReturnsEmpty() {
        var (_, index) = NewIndex();

        Assert.That(index.Get(30), Is.Empty);
    }

    [Test]
    public void Range_BoundaryKeysHaveMultipleEntries_AllOfThemIncluded() {
        var (storage, index) = NewIndex();
        var a1 = storage.Insert(new TestRow(1, 20));
        var a2 = storage.Insert(new TestRow(2, 20));
        var b1 = storage.Insert(new TestRow(3, 30));
        var c1 = storage.Insert(new TestRow(4, 40));
        var c2 = storage.Insert(new TestRow(5, 40));
        index.Register(20, a1);
        index.Register(20, a2);
        index.Register(30, b1);
        index.Register(40, c1);
        index.Register(40, c2);

        var rows = index.Range(20, 40);

        Assert.That(rows.Select(r => r.Id), Is.EquivalentTo(new[] { 1, 2, 3, 4, 5 }));
        Assert.That(rows.Select(r => r.Age), Is.EqualTo(new[] { 20, 20, 30, 40, 40 }));
    }

    [Test]
    public void Deregister_ThenRegisterAgainUnderSameKey_Succeeds() {
        var (storage, index) = NewIndex();
        var a = storage.Insert(new TestRow(1, 30));
        index.Register(30, a);
        index.Deregister(30, a);

        var b = storage.Insert(new TestRow(2, 30));
        index.Register(30, b);

        Assert.That(index.Get(30), Is.EqualTo(new[] { new TestRow(2, 30) }));
    }

    [Test]
    public void Register_AcrossChunkBoundary_MaintainsAscendingOrder() {
        // chunkSize is 4, so 6 inserts force a second chunk allocation partway through.
        var (storage, index) = NewIndex();
        var ages = new[] { 50, 10, 60, 30, 20, 40 }; // out of order on purpose
        for (var i = 0; i < ages.Length; i++) {
            var offset = storage.Insert(new TestRow(i, ages[i]));
            index.Register(ages[i], offset);
        }

        var rows = index.Range(10, 60);

        Assert.That(rows.Select(r => r.Age), Is.EqualTo(new[] { 10, 20, 30, 40, 50, 60 }));
    }
}
