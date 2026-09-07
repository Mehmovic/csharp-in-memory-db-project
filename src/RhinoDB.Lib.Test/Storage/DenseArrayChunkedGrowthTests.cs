namespace RhinoDB.Lib.Storage.Test;

public class DenseArrayChunkedGrowthTests
{
    private readonly record struct TestRow(int Id);

    private const int ChunkSize = 4;

    static private DenseArray<TestRow> NewStore() => new(chunkSize: ChunkSize);

    static private void FillTo(DenseArray<TestRow> store, int count)
    {
        while (store.Count < count) store.Insert(new TestRow(store.Count));
    }

    [Test]
    public void Construct_AllocatesExactlyOneChunkUpfront()
    {
        var store = NewStore();

        Assert.That(store.Count, Is.EqualTo(0));
        Assert.That(store.Capacity, Is.EqualTo(ChunkSize));
    }

    [Test]
    public void Insert_FillingTheFirstChunkExactly_DoesNotGrow()
    {
        var store = NewStore();

        FillTo(store, ChunkSize);

        Assert.That(store.Count, Is.EqualTo(4));
        Assert.That(store.Capacity, Is.EqualTo(4));
    }

    [Test]
    public void Insert_PastTheFirstChunk_AllocatesASecondChunk()
    {
        var store = NewStore();
        FillTo(store, ChunkSize);

        store.Insert(new TestRow(99));

        Assert.That(store.Count, Is.EqualTo(5));
        Assert.That(store.Capacity, Is.EqualTo(8));
        Assert.That(store.Get(0), Is.EqualTo(new TestRow(0)), "growing must not corrupt data already stored in the first chunk");
    }

    [Test]
    public void Insert_FillingTheSecondChunkExactly_DoesNotGrowAgain()
    {
        var store = NewStore();

        FillTo(store, 2 * ChunkSize);

        Assert.That(store.Count, Is.EqualTo(8));
        Assert.That(store.Capacity, Is.EqualTo(8));
    }

    [Test]
    public void Insert_PastTheSecondChunk_AllocatesAThirdChunk()
    {
        var store = NewStore();
        FillTo(store, 2 * ChunkSize);

        store.Insert(new TestRow(99));

        Assert.That(store.Count, Is.EqualTo(9));
        Assert.That(store.Capacity, Is.EqualTo(12));
    }

    [Test]
    public void Delete_AboveTheShrinkThreshold_KeepsTheTrailingChunkAllocated()
    {
        // 2 chunks allocated (Capacity=8), shrink threshold is ChunkSize/2 = 2.
        var store = NewStore();
        FillTo(store, 5); // Count=5, Capacity=8

        store.Delete(4); // Count=4, still above the threshold of 2
        Assert.That(store.Capacity, Is.EqualTo(8));

        store.Delete(3); // Count=3, still above the threshold of 2
        Assert.That(store.Capacity, Is.EqualTo(8));
    }

    [Test]
    public void Delete_DownToHalfOfOneChunk_FreesTheTrailingChunk()
    {
        // Mirrors the worked example: two chunks, second one emptied out by deletes,
        // but only freed once total population drops to half a chunk (not as soon as
        // the second chunk itself is empty) to avoid thrashing on the boundary.
        var store = NewStore();
        FillTo(store, 5); // Count=5, Capacity=8 (2 chunks)

        store.Delete(4);
        store.Delete(3); // Count=3, Capacity still 8

        store.Delete(2); // Count=2 == ChunkSize/2 -> trailing chunk freed

        Assert.That(store.Count, Is.EqualTo(2));
        Assert.That(store.Capacity, Is.EqualTo(4));
    }

    [Test]
    public void Delete_CannotShrinkBelowOneChunk()
    {
        var store = NewStore();
        FillTo(store, 2); // Count=2, Capacity=4 (1 chunk)

        store.Delete(1);
        store.Delete(0);

        Assert.That(store.Count, Is.EqualTo(0));
        Assert.That(store.Capacity, Is.EqualTo(ChunkSize), "a store should always keep at least one chunk allocated");
    }

    [Test]
    public void Shrink_ThenImmediateReinsert_DoesNotThrashCapacityBackUp()
    {
        // Regression guard for the specific failure mode hysteresis exists to prevent:
        // grow-trigger (Count == Capacity) and shrink-trigger must be far enough apart
        // that a single delete followed by a single insert can't bounce capacity.
        var store = NewStore();
        FillTo(store, 5); // Count=5, Capacity=8
        store.Delete(4);
        store.Delete(3);
        store.Delete(2); // Count=2, Capacity shrinks to 4

        store.Insert(new TestRow(100)); // Count=3

        Assert.That(store.Capacity, Is.EqualTo(4), "one re-insert right after a shrink must not immediately regrow capacity");
    }

    [Test]
    public void Delete_DownToHalfOfOneChunk_GeneralizesAcrossMoreThanTwoChunks()
    {
        // Extrapolation of the two-chunk rule to N chunks: shrink from N to N-1 chunks
        // once Count <= Capacity - 1.5 * ChunkSize (i.e. the (N-1)-th chunk would be
        // left only half full). Flagging this as an assumption beyond the given example.
        var store = NewStore();
        FillTo(store, 9); // Count=9, Capacity=12 (3 chunks)

        store.Delete(8);
        store.Delete(7); // Count=7, Capacity still 12

        store.Delete(6); // Count=6 == Capacity(12) - 1.5*ChunkSize(4) -> trailing chunk freed

        Assert.That(store.Count, Is.EqualTo(6));
        Assert.That(store.Capacity, Is.EqualTo(8));
    }

    [Test]
    public void Delete_ViaSwapRemove_UsesTotalCountForShrinkThreshold_NotTheVacatedSlotsLocalPosition()
    {
        // Regression: the shrink decision must depend on total Count vs. the threshold,
        // not on which slot-within-a-chunk happened to get vacated. A swap-remove can
        // vacate an early slot in a chunk while plenty of live elements still remain
        // overall, and that must not trigger an early shrink.
        var store = NewStore();
        FillTo(store, 5); // Count=5, Capacity=8 (2 chunks)

        store.Delete(0); // swap-remove: element at index 4 moves into slot 0. Count=4
        store.Delete(1); // swap-remove: new last element moves into slot 1. Count=3

        Assert.That(store.Count, Is.EqualTo(3));
        Assert.That(store.Capacity, Is.EqualTo(8), "population (3) is still above the shrink threshold (2); must not shrink yet");
    }
}
