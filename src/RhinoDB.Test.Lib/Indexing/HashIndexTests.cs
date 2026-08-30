using RhinoDB.Lib.Indexing;
using RhinoDB.Lib.Storage;

namespace RhinoDB.Test.Lib.Indexing;

public class HashIndexTests
{
    private readonly record struct TestRow(int Id, string Name);

    private static HashIndex<int, TestRow> NewIndex() => new(new DenseArray<TestRow>(chunkSize: 4), row => row.Id);

    [Test]
    public void Insert_ThenGet_ReturnsTheInsertedRow()
    {
        var index = NewIndex();

        index.Insert(new TestRow(1, "Alice"));

        Assert.That(index.Get(1), Is.EqualTo(new TestRow(1, "Alice")));
        Assert.That(index.Count, Is.EqualTo(1));
    }

    [Test]
    public void Insert_MultipleRows_AllRetrievableByKey()
    {
        var index = NewIndex();

        index.Insert(new TestRow(1, "Alice"));
        index.Insert(new TestRow(2, "Bob"));
        index.Insert(new TestRow(3, "Carol"));

        Assert.That(index.Get(1), Is.EqualTo(new TestRow(1, "Alice")));
        Assert.That(index.Get(2), Is.EqualTo(new TestRow(2, "Bob")));
        Assert.That(index.Get(3), Is.EqualTo(new TestRow(3, "Carol")));
        Assert.That(index.Count, Is.EqualTo(3));
    }

    [Test]
    public void Insert_DuplicateKey_Throws()
    {
        var index = NewIndex();
        index.Insert(new TestRow(1, "Alice"));

        Assert.Throws<ArgumentException>(() => index.Insert(new TestRow(1, "Impostor")));
    }

    [Test]
    public void Insert_DuplicateKey_RollsBackTheStorageInsert()
    {
        // Insert always appends, so a rejected duplicate must not leave an orphaned,
        // unreachable row sitting in storage inflating Count.
        var index = NewIndex();
        index.Insert(new TestRow(1, "Alice"));

        Assert.Throws<ArgumentException>(() => index.Insert(new TestRow(1, "Impostor")));

        Assert.That(index.Count, Is.EqualTo(1));
    }

    [Test]
    public void Get_UnknownKey_ThrowsKeyNotFoundException()
    {
        var index = NewIndex();

        Assert.Throws<KeyNotFoundException>(() => index.Get(999));
    }

    [Test]
    public void Delete_RemovesTheKey_SubsequentGetThrows()
    {
        var index = NewIndex();
        index.Insert(new TestRow(1, "Alice"));

        index.Delete(1);

        Assert.That(index.Count, Is.EqualTo(0));
        Assert.Throws<KeyNotFoundException>(() => index.Get(1));
    }

    [Test]
    public void Delete_UnknownKey_ThrowsKeyNotFoundException()
    {
        var index = NewIndex();

        Assert.Throws<KeyNotFoundException>(() => index.Delete(999));
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
        Assert.That(index.Get(3), Is.EqualTo(new TestRow(3, "Carol")), "moved row must still be reachable by its key");
        Assert.That(index.Get(2), Is.EqualTo(new TestRow(2, "Bob")), "untouched row must be unaffected");
        Assert.Throws<KeyNotFoundException>(() => index.Get(1));
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
        Assert.That(index.Get(1), Is.EqualTo(new TestRow(1, "Alice")));
        Assert.That(index.Get(2), Is.EqualTo(new TestRow(2, "Bob")));
        Assert.Throws<KeyNotFoundException>(() => index.Get(3));
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
        Assert.That(index.Get(2), Is.EqualTo(new TestRow(2, "Bob")));
        Assert.That(index.Get(3), Is.EqualTo(new TestRow(3, "Carol")));
    }
}
