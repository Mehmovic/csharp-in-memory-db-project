using RhinoDB.Lib.Indexing;
using RhinoDB.Lib.Storage;

namespace RhinoDB.Test.Lib.Indexing;

public class HashIndexTests
{
    private readonly record struct TestRow(int Id, string Name);

    static private HashIndex<int, TestRow> NewIndex() => new HashIndex<int, TestRow>(
        new DenseArray<TestRow>(chunkSize: 4), row => row.Id);

    [Test]
    public void Insert_ThenGet_ReturnsTheInsertedRow()
    {
        var index = NewIndex();

        var insertResult = index.Insert(new TestRow(1, "Alice"));

        Assert.That(insertResult.IsOk(), Is.True);
        Assert.That(index.Get(1).Unwrap(), Is.EqualTo(new TestRow(1, "Alice")));
        Assert.That(index.Count, Is.EqualTo(1));
    }

    [Test]
    public void Insert_MultipleRows_AllRetrievableByKey()
    {
        var index = NewIndex();

        index.Insert(new TestRow(1, "Alice"));
        index.Insert(new TestRow(2, "Bob"));
        index.Insert(new TestRow(3, "Carol"));

        Assert.That(index.Get(1).Unwrap(), Is.EqualTo(new TestRow(1, "Alice")));
        Assert.That(index.Get(2).Unwrap(), Is.EqualTo(new TestRow(2, "Bob")));
        Assert.That(index.Get(3).Unwrap(), Is.EqualTo(new TestRow(3, "Carol")));
        Assert.That(index.Count, Is.EqualTo(3));
    }

    [Test]
    public void Insert_DuplicateKey_ReturnsFailureWithArgumentException()
    {
        var index = NewIndex();
        index.Insert(new TestRow(1, "Alice"));

        var result = index.Insert(new TestRow(1, "Impostor"));

        Assert.That(result.IsError, Is.True);
        Assert.That(result.GetException(), Is.InstanceOf<ArgumentException>());
    }

    [Test]
    public void Insert_DuplicateKey_RollsBackTheStorageInsert()
    {
        // Insert always appends, so a rejected duplicate must not leave an orphaned,
        // unreachable row sitting in storage inflating Count.
        var index = NewIndex();
        index.Insert(new TestRow(1, "Alice"));

        var result = index.Insert(new TestRow(1, "Impostor"));

        Assert.That(result.IsError(), Is.True);
        Assert.That(index.Count, Is.EqualTo(1));
    }

    [Test]
    public void Get_UnknownKey_ReturnsFailureWithKeyNotFoundException()
    {
        var index = NewIndex();

        var result = index.Get(999);

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetException(), Is.InstanceOf<KeyNotFoundException>());
    }

    [Test]
    public void Delete_RemovesTheKey_SubsequentGetReturnsFailure()
    {
        var index = NewIndex();
        index.Insert(new TestRow(1, "Alice"));

        var deleteResult = index.Delete(1);

        Assert.That(deleteResult.IsOk(), Is.True);
        Assert.That(index.Count, Is.EqualTo(0));
        Assert.That(index.Get(1).IsError(), Is.True);
    }

    [Test]
    public void Delete_UnknownKey_ReturnsFailureWithKeyNotFoundException()
    {
        var index = NewIndex();

        var result = index.Delete(999);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.GetException(), Is.InstanceOf<KeyNotFoundException>());
    }

    [Test]
    public void Delete_OfNonLastRow_RepointsTheMovedRowsIndex()
    {
        // The underlying DenseArray swap-removes: the last row physically moves into
        // the deleted row's slot. The index must repoint that row's dictionary entry
        // to its new slot, or its key becomes unreachable / points at stale data.
        var index = NewIndex();
        index.Insert(new TestRow(1, "Alice"));
        index.Insert(new TestRow(2, "Bob"));
        index.Insert(new TestRow(3, "Carol"));

        index.Delete(1); // Carol (physically last) moves into Alice's old slot

        Assert.That(index.Count, Is.EqualTo(2));
        Assert.That(index.Get(3).Unwrap(), Is.EqualTo(new TestRow(3, "Carol")), "moved row must still be reachable by its key");
        Assert.That(index.Get(2).Unwrap(), Is.EqualTo(new TestRow(2, "Bob")), "untouched row must be unaffected");
        Assert.That(index.Get(1).IsError(), Is.True);
    }

    [Test]
    public void Delete_OfThePhysicallyLastRow_NeedsNoRepointing()
    {
        var index = NewIndex();
        index.Insert(new TestRow(1, "Alice"));
        index.Insert(new TestRow(2, "Bob"));
        index.Insert(new TestRow(3, "Carol"));

        index.Delete(3); // Carol was already physically last; nothing moves

        Assert.That(index.Count, Is.EqualTo(2));
        Assert.That(index.Get(1).Unwrap(), Is.EqualTo(new TestRow(1, "Alice")));
        Assert.That(index.Get(2).Unwrap(), Is.EqualTo(new TestRow(2, "Bob")));
        Assert.That(index.Get(3).IsError(), Is.True);
    }

    [Test]
    public void InsertAfterDelete_ReusesTheFreedSlot_AndIndexStaysConsistent()
    {
        var index = NewIndex();
        index.Insert(new TestRow(1, "Alice"));
        index.Insert(new TestRow(2, "Bob"));

        index.Delete(1); // Bob moves into slot 0
        index.Insert(new TestRow(3, "Carol")); // appended into the now-freed slot 1

        Assert.That(index.Count, Is.EqualTo(2));
        Assert.That(index.Get(2).Unwrap(), Is.EqualTo(new TestRow(2, "Bob")));
        Assert.That(index.Get(3).Unwrap(), Is.EqualTo(new TestRow(3, "Carol")));
    }
}
