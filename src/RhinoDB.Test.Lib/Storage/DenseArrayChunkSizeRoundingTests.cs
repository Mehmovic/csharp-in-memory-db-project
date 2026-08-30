using RhinoDB.Lib.Storage;

namespace RhinoDB.Test.Lib.Storage;

public class DenseArrayChunkSizeRoundingTests
{
    private readonly record struct TestRow(int Id);

    private static void FillTo(DenseArray<TestRow> store, int count)
    {
        while (store.Count < count) store.Insert(new TestRow(store.Count));
    }

    [TestCase(1, 1)]
    [TestCase(2, 2)]
    [TestCase(3, 4)]
    [TestCase(4, 4)]
    [TestCase(5, 8)]
    [TestCase(6, 8)]
    [TestCase(8, 8)]
    [TestCase(9, 16)]
    [TestCase(1023, 1024)]
    [TestCase(1024, 1024)]
    public void Construct_RoundsRequestedChunkSizeUpToTheNextPowerOfTwo(int requestedChunkSize, int expectedChunkSize)
    {
        var store = new DenseArray<TestRow>(chunkSize: requestedChunkSize);

        Assert.That(store.Count, Is.EqualTo(0));
        Assert.That(store.Capacity, Is.EqualTo(expectedChunkSize), "capacity of a fresh store is exactly one chunk");
    }

    [Test]
    public void Insert_PastARoundedChunkSize_GrowsByTheRoundedAmount()
    {
        var store = new DenseArray<TestRow>(chunkSize: 5); // rounds up to 8

        FillTo(store, 8);
        Assert.That(store.Capacity, Is.EqualTo(8));

        store.Insert(new TestRow(99));
        Assert.That(store.Capacity, Is.EqualTo(16));
    }

    [Test]
    public void InsertAndGet_BehaveCorrectlyAcrossARoundedChunkBoundary()
    {
        var store = new DenseArray<TestRow>(chunkSize: 3); // rounds up to 4

        FillTo(store, 5); // spans into a second rounded chunk

        Assert.That(store.Get(0), Is.EqualTo(new TestRow(0)));
        Assert.That(store.Get(3), Is.EqualTo(new TestRow(3)));
        Assert.That(store.Get(4), Is.EqualTo(new TestRow(4)));
    }
}
