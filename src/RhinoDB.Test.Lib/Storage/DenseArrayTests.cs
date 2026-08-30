using RhinoDB.Lib.Storage;

namespace RhinoDB.Test.Lib.Storage;

public class DenseArrayTests
{
    private readonly record struct TestRow(int Id, string Name);

    [Test]
    public void Insert_ReturnsSequentialIndices()
    {
        var store = new DenseArray<TestRow>(chunkSize: 1);

        var first = store.Insert(new TestRow(1, "Alice"));
        var second = store.Insert(new TestRow(2, "Bob"));
        var third = store.Insert(new TestRow(3, "Carol"));

        Assert.That(first, Is.EqualTo(0));
        Assert.That(second, Is.EqualTo(1));
        Assert.That(third, Is.EqualTo(2));
        Assert.That(store.Count, Is.EqualTo(3));
    }

    [Test]
    public void Insert_ValueIsRetrievableByReturnedIndex()
    {
        var store = new DenseArray<TestRow>(chunkSize: 1);

        var index = store.Insert(new TestRow(42, "Dana"));

        Assert.That(store.Get(index), Is.EqualTo(new TestRow(42, "Dana")));
    }

    [Test]
    public void Delete_OfNonLastElement_SwapsLastElementIntoItsSlot()
    {
        var store = new DenseArray<TestRow>(chunkSize: 1);
        var a = store.Insert(new TestRow(1, "A"));
        var b = store.Insert(new TestRow(2, "B"));
        var c = store.Insert(new TestRow(3, "C"));

        store.Delete(a);

        Assert.That(store.Count, Is.EqualTo(2));
        Assert.That(store.Get(a), Is.EqualTo(new TestRow(3, "C")));
        Assert.That(store.Get(b), Is.EqualTo(new TestRow(2, "B")));
        _ = c;
    }

    [Test]
    public void Delete_OfLastElement_JustShrinksWithoutMovingAnything()
    {
        var store = new DenseArray<TestRow>(chunkSize: 1);
        var a = store.Insert(new TestRow(1, "A"));
        var b = store.Insert(new TestRow(2, "B"));
        var c = store.Insert(new TestRow(3, "C"));

        store.Delete(c);

        Assert.That(store.Count, Is.EqualTo(2));
        Assert.That(store.Get(a), Is.EqualTo(new TestRow(1, "A")));
        Assert.That(store.Get(b), Is.EqualTo(new TestRow(2, "B")));
    }

    [Test]
    public void Delete_OnlyElement_EmptiesTheStore()
    {
        var store = new DenseArray<TestRow>(chunkSize: 1);
        var a = store.Insert(new TestRow(1, "A"));

        store.Delete(a);

        Assert.That(store.Count, Is.EqualTo(0));
    }

    [Test]
    public void InsertAfterDelete_AppendsPastCurrentCount_NoGapReused()
    {
        var store = new DenseArray<TestRow>(chunkSize: 1);
        var a = store.Insert(new TestRow(1, "A"));
        store.Insert(new TestRow(2, "B"));

        store.Delete(a); // B swaps into slot 0, Count becomes 1
        var c = store.Insert(new TestRow(3, "C"));

        Assert.That(c, Is.EqualTo(1));
        Assert.That(store.Count, Is.EqualTo(2));
        Assert.That(store.Get(0), Is.EqualTo(new TestRow(2, "B")));
        Assert.That(store.Get(1), Is.EqualTo(new TestRow(3, "C")));
    }
}
