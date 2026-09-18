namespace RhinoDB.Lib.Storage.Test;

// DeleteMany batches a whole set of removals into one pass: sort the targets, then
// only pull a replacement row from the shrinking tail when that tail position isn't
// itself a target - unlike calling Delete(offset) once per target, which can relocate
// a row that's ALSO a later target in the same batch (wasted work: it just gets
// deleted again moments later). See DenseArrayDeleteManyTests.DeleteMany_ScatteredTargets_ProducesTheMinimalRelocationSet
// for the exact scenario that distinguishes the two.
public class DenseArrayDeleteManyTests {
    private readonly record struct TestRow(int Id, string Name);

    static private DenseArray<TestRow> NewStoreWith(int count, int chunkSize = 1) {
        var store = new DenseArray<TestRow>(chunkSize);
        for (var i = 0; i < count; i++) store.Insert(new TestRow(i, $"R{i}"));
        return store;
    }

    [Test]
    public void DeleteMany_WithEmptyOffsets_IsANoOp() {
        var store = NewStoreWith(5);

        using var relocations = store.DeleteMany([]);

        Assert.That(relocations.Count, Is.Zero);
        Assert.That(store.Count, Is.EqualTo(5));
        for (var i = 0; i < 5; i++) Assert.That(store.Get(i), Is.EqualTo(new TestRow(i, $"R{i}")));
    }

    [Test]
    public void DeleteMany_AllOffsets_EmptiesTheStoreWithNoRelocations() {
        var store = NewStoreWith(5);

        using var relocations = store.DeleteMany([0, 1, 2, 3, 4]);

        Assert.That(store.Count, Is.EqualTo(0));
        Assert.That(relocations.Count, Is.Zero, "Deleting every row needs no swaps at all - nothing survives to relocate.");
    }

    [Test]
    public void DeleteMany_OfTargetsAlreadyAtTheTail_NeedsNoRelocations() {
        // Deleting the physically-last 3 of 5 rows: every target is already >= the
        // shrunk Count, so each one is handled by the Count decrease alone.
        var store = NewStoreWith(5);

        using var relocations = store.DeleteMany([2, 3, 4]);

        Assert.That(store.Count, Is.EqualTo(2));
        Assert.That(relocations.Count, Is.Zero);
        Assert.That(store.Get(0), Is.EqualTo(new TestRow(0, "R0")));
        Assert.That(store.Get(1), Is.EqualTo(new TestRow(1, "R1")));
    }

    [Test]
    public void DeleteMany_ScatteredTargets_ProducesTheMinimalRelocationSet() {
        // 10 rows (R0..R9), delete offsets {2, 5, 8}. newCount = 7.
        // Naive one-at-a-time Delete(offset) calls (in any order) touch R9 or R8
        // TWICE here - the row swapped into an earlier target can itself land on
        // (or already be) a later target still pending in the batch. The batched
        // algorithm recognizes target 8 is already >= newCount (auto-chopped, no
        // swap needed) and never uses it as a relocation source, so only 2
        // relocations happen for 3 deletions, not 3 - and R8 is never touched.
        var store = NewStoreWith(10);

        using var relocations = store.DeleteMany([2, 5, 8]);

        Assert.That(store.Count, Is.EqualTo(7));
        Assert.That(relocations.Count, Is.EqualTo(2), "Target 8 was already past the shrunk boundary - it needs no swap.");
        Assert.That(relocations.Buffer().ToArray(), Is.EquivalentTo(new[] {
            new DenseArrayRelocation<TestRow>(new TestRow(9, "R9"), 9, 2),
            new DenseArrayRelocation<TestRow>(new TestRow(7, "R7"), 7, 5),
        }));

        // Deleted rows are gone.
        var survivors = new List<TestRow>();
        for (var i = 0; i < store.Count; i++) survivors.Add(store.Get(i));
        Assert.That(survivors, Has.None.Matches<TestRow>(r => r.Id is 2 or 5 or 8));

        // Untouched rows stayed exactly where they were.
        Assert.That(store.Get(0), Is.EqualTo(new TestRow(0, "R0")));
        Assert.That(store.Get(1), Is.EqualTo(new TestRow(1, "R1")));
        Assert.That(store.Get(3), Is.EqualTo(new TestRow(3, "R3")));
        Assert.That(store.Get(4), Is.EqualTo(new TestRow(4, "R4")));
        Assert.That(store.Get(6), Is.EqualTo(new TestRow(6, "R6")));

        // Relocated rows landed exactly where reported.
        Assert.That(store.Get(2), Is.EqualTo(new TestRow(9, "R9")));
        Assert.That(store.Get(5), Is.EqualTo(new TestRow(7, "R7")));
    }

    [Test]
    public void DeleteMany_ReportedRelocations_MatchTheActualFinalState() {
        var store = NewStoreWith(20);

        using var relocations = store.DeleteMany([1, 4, 6, 9, 13, 17]);

        foreach (var relocation in relocations)
            Assert.That(store.Get(relocation.NewOffset), Is.EqualTo(relocation.Row),
                $"Relocation reported {relocation.Row} at {relocation.NewOffset}, but that's not what's actually there.");
    }

    [Test]
    public void DeleteMany_EveryRelocatedRowAppearsAtMostOnce() {
        // The property the whole batched algorithm exists to guarantee: no row is
        // ever relocated twice within one DeleteMany call.
        var store = NewStoreWith(50);
        var targets = new[] { 0, 3, 7, 8, 12, 19, 20, 21, 30, 31, 45, 49 };

        using var relocations = store.DeleteMany(targets);

        var rowIds = new List<int>();
        foreach (var relocation in relocations) rowIds.Add(relocation.Row.Id);
        Assert.That(rowIds, Is.Unique);
    }

    [Test]
    public void DeleteMany_SingleNonLastOffset_MatchesSingleDeleteBehavior() {
        var bulkStore = NewStoreWith(3);
        var singleStore = NewStoreWith(3);

        using var relocations = bulkStore.DeleteMany([0]);
        singleStore.Delete(0);

        Assert.That(bulkStore.Count, Is.EqualTo(singleStore.Count));
        Assert.That(bulkStore.Get(0), Is.EqualTo(singleStore.Get(0)));
        Assert.That(relocations.Count, Is.EqualTo(1));
        Assert.That(relocations.Buffer()[0], Is.EqualTo(new DenseArrayRelocation<TestRow>(new TestRow(2, "R2"), 2, 0)));
    }

    [Test]
    public void DeleteMany_SingleLastOffset_MatchesSingleDeleteBehavior() {
        var bulkStore = NewStoreWith(3);

        using var relocations = bulkStore.DeleteMany([2]);

        Assert.That(bulkStore.Count, Is.EqualTo(2));
        Assert.That(relocations.Count, Is.Zero, "Deleting the physically-last row never needs a swap.");
    }

    [Test]
    public void DeleteMany_UnsortedOffsetsInput_StillWorksCorrectly() {
        var store = NewStoreWith(10);

        using var relocations = store.DeleteMany([8, 2, 5]); // same targets as the scattered test, given out of order

        Assert.That(store.Count, Is.EqualTo(7));
        Assert.That(relocations.Count, Is.EqualTo(2));
        Assert.That(store.Get(2), Is.EqualTo(new TestRow(9, "R9")));
        Assert.That(store.Get(5), Is.EqualTo(new TestRow(7, "R7")));
    }

    [Test]
    public void DeleteMany_AcrossMultipleChunks_StillCorrect() {
        // chunkSize rounds up to a power of 2; 4 forces several chunk boundaries
        // across 20 rows, exercising the chunk-index math on both the read side
        // (fetching a relocation source) and the write side (placing it).
        var store = NewStoreWith(20, chunkSize: 4);

        using var relocations = store.DeleteMany([1, 4, 6, 9, 13, 17]);

        Assert.That(store.Count, Is.EqualTo(14));
        foreach (var relocation in relocations)
            Assert.That(store.Get(relocation.NewOffset), Is.EqualTo(relocation.Row));

        var survivorIds = new List<int>();
        for (var i = 0; i < store.Count; i++) survivorIds.Add(store.Get(i).Id);
        Assert.That(survivorIds, Has.None.Matches<int>(id => id is 1 or 4 or 6 or 9 or 13 or 17));
        Assert.That(survivorIds, Has.Count.EqualTo(14));
    }

    [Test]
    public void DeleteMany_OnlyElement_EmptiesTheStore() {
        var store = NewStoreWith(1);

        using var relocations = store.DeleteMany([0]);

        Assert.That(store.Count, Is.EqualTo(0));
        Assert.That(relocations.Count, Is.Zero);
    }
}
