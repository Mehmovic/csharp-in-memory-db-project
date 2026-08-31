using RhinoDB.Core.Exceptions;
using RhinoDB.Lib.Indexing;
using RhinoDB.Lib.Storage;

namespace RhinoDB.Test.Lib.Indexing;

public class NonUniqueHashIndexTests
{
    private readonly record struct TestRow(int Id, string Team);

    static private (DenseArray<TestRow> Storage, NonUniqueHashIndex<string, TestRow> Index) NewIndex() {
        var storage = new DenseArray<TestRow>(chunkSize: 4);
        return (storage, new NonUniqueHashIndex<string, TestRow>(storage));
    }

    [Test]
    public void Get_UnknownKey_ReturnsEmpty()
    {
        var (_, index) = NewIndex();

        Assert.That(index.Get("Red"), Is.Empty);
    }

    [Test]
    public void Register_ThenGet_ReturnsTheRegisteredRow()
    {
        var (storage, index) = NewIndex();
        var offset = storage.Insert(new TestRow(1, "Red"));

        index.Register("Red", offset);

        Assert.That(index.Get("Red"), Is.EqualTo(new[] { new TestRow(1, "Red") }));
    }

    [Test]
    public void Register_MultipleRowsSameKey_GetReturnsAllOfThem()
    {
        var (storage, index) = NewIndex();
        var a = storage.Insert(new TestRow(1, "Red"));
        var b = storage.Insert(new TestRow(2, "Red"));
        var c = storage.Insert(new TestRow(3, "Blue"));

        index.Register("Red", a);
        index.Register("Red", b);
        index.Register("Blue", c);

        Assert.That(index.Get("Red"), Is.EquivalentTo(new[] { new TestRow(1, "Red"), new TestRow(2, "Red") }));
        Assert.That(index.Get("Blue"), Is.EquivalentTo(new[] { new TestRow(3, "Blue") }));
    }

    [Test]
    public void Deregister_RemovesOnlyTheGivenOffset_OtherRowsWithSameKeyRemain()
    {
        // Three offsets under one key exercises the swap-pop path in Deregister
        // (removing the middle entry swaps the last one into its place).
        var (storage, index) = NewIndex();
        var a = storage.Insert(new TestRow(1, "Red"));
        var b = storage.Insert(new TestRow(2, "Red"));
        var c = storage.Insert(new TestRow(3, "Red"));
        index.Register("Red", a);
        index.Register("Red", b);
        index.Register("Red", c);

        index.Deregister("Red", b);

        Assert.That(index.Get("Red"), Is.EquivalentTo(new[] { new TestRow(1, "Red"), new TestRow(3, "Red") }));
    }

    [Test]
    public void Deregister_LastOffsetUnderAKey_KeyNoLongerAppearsInGet()
    {
        var (storage, index) = NewIndex();
        var offset = storage.Insert(new TestRow(1, "Red"));
        index.Register("Red", offset);

        index.Deregister("Red", offset);

        Assert.That(index.Get("Red"), Is.Empty);
    }

    [Test]
    public void Deregister_UnknownKey_ReturnsFailureWithIndexKeyNotFoundException()
    {
        var (_, index) = NewIndex();

        var res = index.Deregister("Red", 0);

        Assert.That(res.IsError(), Is.True);
        Assert.That(res.GetException(), Is.InstanceOf<IndexKeyNotFoundException>());
    }

    [Test]
    public void Deregister_OffsetNotUnderTheGivenKey_ReturnsFailureWithOffsetNotRegisteredException()
    {
        var (storage, index) = NewIndex();
        var offset = storage.Insert(new TestRow(1, "Red"));
        index.Register("Red", offset);

        var res = index.Deregister("Red", 999);

        Assert.That(res.IsError(), Is.True);
        Assert.That(res.GetException(), Is.InstanceOf<OffsetNotRegisteredException>());
    }

    [Test]
    public void Deregister_FirstOfTwo_RemainingRowStillRetrievable()
    {
        var (storage, index) = NewIndex();
        var a = storage.Insert(new TestRow(1, "Red"));
        var b = storage.Insert(new TestRow(2, "Red"));
        index.Register("Red", a);
        index.Register("Red", b);

        index.Deregister("Red", a); // first entry - swaps the last element into its place

        Assert.That(index.Get("Red"), Is.EqualTo(new[] { new TestRow(2, "Red") }));
    }

    [Test]
    public void Deregister_LastPositionedOfMultiple_LeavesOthersIntact()
    {
        // Removing the physically-last entry in the bucket is the self-swap edge case
        // (position == lastIndex): offsets[position] = offsets[lastIndex] is a no-op
        // before RemoveAt, and must not corrupt the remaining entries.
        var (storage, index) = NewIndex();
        var a = storage.Insert(new TestRow(1, "Red"));
        var b = storage.Insert(new TestRow(2, "Red"));
        var c = storage.Insert(new TestRow(3, "Red"));
        index.Register("Red", a);
        index.Register("Red", b);
        index.Register("Red", c);

        index.Deregister("Red", c); // last registered - lands at the bucket's last position

        Assert.That(index.Get("Red"), Is.EquivalentTo(new[] { new TestRow(1, "Red"), new TestRow(2, "Red") }));
    }

    [Test]
    public void Deregister_ThenRegisterAgainUnderSameKey_RecreatesTheBucket()
    {
        var (storage, index) = NewIndex();
        var a = storage.Insert(new TestRow(1, "Red"));
        index.Register("Red", a);
        index.Deregister("Red", a); // bucket is now removed entirely

        var b = storage.Insert(new TestRow(2, "Red"));
        index.Register("Red", b);

        Assert.That(index.Get("Red"), Is.EqualTo(new[] { new TestRow(2, "Red") }));
    }

    [Test]
    public void Register_AcrossChunkBoundary_AllRowsRemainRetrievableByKey()
    {
        // chunkSize is 4, so 6 inserts force a second chunk allocation partway through.
        var (storage, index) = NewIndex();
        for (var i = 1; i <= 6; i++) {
            var offset = storage.Insert(new TestRow(i, "Red"));
            index.Register("Red", offset);
        }

        Assert.That(index.Get("Red"), Is.EquivalentTo(Enumerable.Range(1, 6).Select(i => new TestRow(i, "Red"))));
    }
}
