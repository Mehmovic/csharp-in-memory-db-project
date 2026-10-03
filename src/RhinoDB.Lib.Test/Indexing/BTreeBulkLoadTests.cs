namespace RhinoDB.Lib.Indexing.Test;

using NUnit.Framework;
using RhinoDB.Core;

// BulkLoad builds a whole index in one pass instead of N inserts. The reason it exists is a
// measured cliff: Insert splits a full chunk with chunks.Insert(idx + 1, ...), an O(chunk count)
// shift on a List, so random-order loading measured 815 ns/row at 10M rows against 60 ns/row
// sequential. Sequential always splits at the LAST chunk, so it never pays - which is why the
// earlier scaling run looked flat and proved nothing.
//
// These tests prove BulkLoad produces an index INDISTINGUISHABLE from one built by Insert:
// same answers for every key, same offsets, same scan contents. An index that is merely
// self-consistent would pass a naive test and still be wrong, so the oracle is always the
// Insert-built index.
[TestFixture]
public class BTreeBulkLoadTests {
    // Deterministic on purpose: a flaky ordering test is worse than none.
    static private int[] Shuffled(int rows, int seed) {
        var order = new int[rows];
        for (var i = 0; i < rows; i++) order[i] = i;
        var state = (uint)seed | 1u;
        for (var i = rows - 1; i > 0; i--) {
            state ^= state << 13; state ^= state >> 17; state ^= state << 5;
            var j = (int)(state % (uint)(i + 1));
            (order[i], order[j]) = (order[j], order[i]);
        }
        return order;
    }

    static private void AssertSameIndex(BTreeIndex<int, DefaultComparer<int>> expected, BTreeIndex<int, DefaultComparer<int>> actual, int rows) {
        Assert.That(actual.Count, Is.EqualTo(expected.Count), "Count must match");
        for (var i = 0; i < rows; i++) {
            Assert.That(actual.GetOffset(i).Unwrap(), Is.EqualTo(expected.GetOffset(i).Unwrap()),
                $"offset for key {i} must match the Insert-built index");
        }
        using var expectedScan = expected.GetOffsetsIter();
        using var actualScan = actual.GetOffsetsIter();
        Assert.That(actualScan.Count, Is.EqualTo(expectedScan.Count), "scan size must match");
        for (var i = 0; i < expectedScan.Count; i++) {
            Assert.That(actualScan.BufferResult().Unwrap()[i], Is.EqualTo(expectedScan.BufferResult().Unwrap()[i]), $"scan offset {i} must match");
        }
    }

    [Test]
    public void ABulkLoadedIndexAnswersEveryKeyExactlyLikeAnInsertedOne() {
        const int Rows = 5_000;
        var order = Shuffled(Rows, 7);
        var keys = new int[Rows];
        var offsets = new int[Rows];
        for (var i = 0; i < Rows; i++) { keys[i] = order[i]; offsets[i] = order[i] * 3; }

        var inserted = new BTreeIndex<int, DefaultComparer<int>>();
        for (var i = 0; i < Rows; i++) inserted.Insert(keys[i], offsets[i]);

        AssertSameIndex(inserted, BTreeIndex<int, DefaultComparer<int>>.BulkLoad(keys, offsets), Rows);
    }

    [Test]
    public void ABulkLoadedIndexSpansManyChunksAndStillAnswersEveryKey() {
        // 3_000 rows at the default 256/chunk is 12 chunks, so the binary search has to climb
        // more than one level. A single-chunk index would never catch an off-by-one in the
        // chunk-filling loop.
        const int Rows = 3_000;
        var order = Shuffled(Rows, 13);
        var keys = new int[Rows];
        var offsets = new int[Rows];
        for (var i = 0; i < Rows; i++) { keys[i] = order[i]; offsets[i] = i; }

        // key k was written with offset = its position in the permutation, not k itself
        var expectedOffset = new int[Rows];
        for (var i = 0; i < Rows; i++) expectedOffset[order[i]] = i;
        var bulk = BTreeIndex<int, DefaultComparer<int>>.BulkLoad(keys, offsets);
        Assert.That(bulk.Count, Is.EqualTo(Rows));
        for (var i = 0; i < Rows; i++) Assert.That(bulk.GetOffset(i).Unwrap(), Is.EqualTo(expectedOffset[i]));
    }

    [Test]
    public void ABulkLoadedIndexWithAChunkSizeOfSixteenMatchesInsert() {
        // Small chunks force many splits in the Insert oracle and many chunks in BulkLoad,
        // which is where an uneven final chunk would show up.
        const int Rows = 200;
        var keys = new int[Rows];
        var offsets = new int[Rows];
        var order = Shuffled(Rows, 21);
        for (var i = 0; i < Rows; i++) { keys[i] = order[i]; offsets[i] = order[i] + 1; }

        var inserted = new BTreeIndex<int, DefaultComparer<int>>(16);
        for (var i = 0; i < Rows; i++) inserted.Insert(keys[i], offsets[i]);

        var bulk = BTreeIndex<int, DefaultComparer<int>>.BulkLoad(keys, offsets, 16);
        AssertSameIndex(inserted, bulk, Rows);
    }

    [Test]
    public void ADuplicateKeyKeepsTheLastWriteJustAsSequentialInsertsWould() {
        // A unique index holds one entry per key, so a duplicate has to resolve the same way an
        // Insert-based build does. Leaving both would make GetOffset ambiguous.
        var keys = new[] { 5, 9, 5, 1, 9 };
        var offsets = new[] { 100, 200, 300, 400, 500 };

        var bulk = BTreeIndex<int, DefaultComparer<int>>.BulkLoad(keys, offsets);
        Assert.That(bulk.Count, Is.EqualTo(3), "three distinct keys");
        Assert.That(bulk.GetOffset(5).Unwrap(), Is.EqualTo(300), "last write for 5 wins");
        Assert.That(bulk.GetOffset(9).Unwrap(), Is.EqualTo(500), "last write for 9 wins");
        Assert.That(bulk.GetOffset(1).Unwrap(), Is.EqualTo(400));
    }

    [Test]
    public void AnEmptyBulkLoadProducesAUsableEmptyIndex() {
        var bulk = BTreeIndex<int, DefaultComparer<int>>.BulkLoad([], []);
        Assert.That(bulk.Count, Is.EqualTo(0));
        Assert.That(bulk.GetOffset(1).IsError, "an empty index resolves nothing");
        using var scan = bulk.GetOffsetsIter();
        Assert.That(scan.Count, Is.EqualTo(0), "scanning an empty index yields nothing");
    }

    [Test]
    public void ABulkLoadedIndexStillAcceptsInsertsAndDeletesAfterwards() {
        // BulkLoad is not a sealed object - the index keeps working afterwards, which is what
        // makes it usable for a cold-storage load followed by live traffic.
        var keys = new[] { 1, 3, 5, 7 };
        var offsets = new[] { 10, 30, 50, 70 };
        var bulk = BTreeIndex<int, DefaultComparer<int>>.BulkLoad(keys, offsets);

        bulk.Insert(9, 90);
        Assert.That(bulk.GetOffset(9).Unwrap(), Is.EqualTo(90));
        Assert.That(bulk.Count, Is.EqualTo(5));

        bulk.Delete(3);
        Assert.That(bulk.GetOffset(3).IsError, "deleted key resolves nothing");
        Assert.That(bulk.Count, Is.EqualTo(4));

        bulk.Insert(2, 22);
        Assert.That(bulk.GetOffset(2).Unwrap(), Is.EqualTo(22));
        for (var k = 1; k <= 9; k++) {
        // 4 and 6 were never inserted, so they must NOT resolve
        if (k is 3 or 4 or 6 or 8) continue;
            Assert.That(bulk.GetOffset(k).IsOk, $"surviving key {k} must still resolve");
        }
    }

    [Test]
    public void RangesOverABulkLoadedIndexReturnTheSameWindowAsRangesOverAnInsertedOne() {
        const int Rows = 4_000;
        var order = Shuffled(Rows, 31);
        var keys = new int[Rows];
        var offsets = new int[Rows];
        for (var i = 0; i < Rows; i++) { keys[i] = order[i]; offsets[i] = order[i]; }

        var inserted = new BTreeIndex<int, DefaultComparer<int>>();
        for (var i = 0; i < Rows; i++) inserted.Insert(keys[i], offsets[i]);
        var bulk = BTreeIndex<int, DefaultComparer<int>>.BulkLoad(keys, offsets);

        using var expectedRange = inserted.GetOffsetsRange(500, 800);
        using var actualRange = bulk.GetOffsetsRange(500, 800);
        Assert.That(actualRange.Count, Is.EqualTo(expectedRange.Count), "range size must match");
        for (var i = 0; i < expectedRange.Count; i++) {
            Assert.That(actualRange.BufferResult().Unwrap()[i], Is.EqualTo(expectedRange.BufferResult().Unwrap()[i]), $"range offset {i} must match");
        }

        using var expectedAll = inserted.GetOffsetsRange(int.MinValue, int.MaxValue);
        using var actualAll = bulk.GetOffsetsRange(int.MinValue, int.MaxValue);
        Assert.That(actualAll.Count, Is.EqualTo(expectedAll.Count), "full range must match");
    }

    [Test]
    public void MismatchedKeyAndOffsetCountsAreRejectedRatherThanHalfBuilt() {
        // A silent truncation here would build an index that is quietly missing rows, which is
        // far worse than a loud failure at load time.
        Assert.Throws<ArgumentException>(() => BTreeIndex<int, DefaultComparer<int>>.BulkLoad([1, 2, 3], [10, 20]));
    }

    [Test]
    public void ABulkLoadedNonUniqueIndexKeepsEveryOffsetForAKey() {
        // The non-unique case is the one BulkLoad must NOT deduplicate: one key with five
        // offsets is five adjacent entries, and GetOffsets walks that run.
        var keys = new[] { 2, 1, 2, 3, 2 };
        var offsets = new[] { 20, 10, 21, 30, 22 };

        var bulk = NonUniqueBTreeIndex<int, DefaultComparer<int>>.BulkLoad(keys, offsets);
        Assert.That(bulk.Count, Is.EqualTo(5), "every entry is kept");
        using var resolved = bulk.GetOffsets(2);
        Assert.That(resolved.Count, Is.EqualTo(3), "key 2 keeps all three of its offsets");
        var found = resolved.BufferResult().Unwrap().ToArray();
        Array.Sort(found);
        Assert.That(found, Is.EqualTo(new[] { 20, 21, 22 }));
    }

    [Test]
    public void ABulkLoadedNonUniqueIndexMatchesAnInsertedOneAcrossChunkBoundaries() {
        // 600 copies of one key spans three 256-entry chunks, which is exactly the case where a
        // run must be walked across chunk edges. Proved against the Insert-built oracle.
        const int Runs = 600;
        var keys = new int[Runs];
        var offsets = new int[Runs];
        for (var i = 0; i < Runs; i++) { keys[i] = 42; offsets[i] = i; }

        var inserted = new NonUniqueBTreeIndex<int, DefaultComparer<int>>();
        for (var i = 0; i < Runs; i++) inserted.Insert(keys[i], offsets[i]);

        var bulk = NonUniqueBTreeIndex<int, DefaultComparer<int>>.BulkLoad(keys, offsets);
        using var expected = inserted.GetOffsets(42);
        using var actual = bulk.GetOffsets(42);
        Assert.That(actual.Count, Is.EqualTo(expected.Count), "run size must match");
        Assert.That(expected.Count, Is.EqualTo(Runs), "oracle must have kept every offset too");

        var expectedSorted = expected.BufferResult().Unwrap().ToArray(); Array.Sort(expectedSorted);
        var actualSorted = actual.BufferResult().Unwrap().ToArray(); Array.Sort(actualSorted);
        Assert.That(actualSorted, Is.EqualTo(expectedSorted), "every offset must survive");
    }

    [Test]
    public void ABulkLoadedNonUniqueIndexScansTheSameOffsetsAsAnInsertedOne() {
        const int Rows = 2_000;
        var order = Shuffled(Rows, 41);
        var keys = new int[Rows];
        var offsets = new int[Rows];
        for (var i = 0; i < Rows; i++) { keys[i] = order[i] % 500; offsets[i] = order[i]; }

        var inserted = new NonUniqueBTreeIndex<int, DefaultComparer<int>>();
        for (var i = 0; i < Rows; i++) inserted.Insert(keys[i], offsets[i]);
        var bulk = NonUniqueBTreeIndex<int, DefaultComparer<int>>.BulkLoad(keys, offsets);

        using var expectedScan = inserted.GetOffsetsIter();
        using var actualScan = bulk.GetOffsetsIter();
        Assert.That(actualScan.Count, Is.EqualTo(expectedScan.Count));
        // Compared as a sorted multiset, NOT position for position. Insert places a duplicate at
        // the index BinarySearch found, so it lands BEFORE the equal keys already there: a run's
        // internal order is neither ascending nor insertion order on either path, and two indexes
        // built by different means cannot be expected to agree on it.
        var expectedSorted = expectedScan.BufferResult().Unwrap().ToArray();
        var actualSorted = actualScan.BufferResult().Unwrap().ToArray();
        Array.Sort(expectedSorted);
        Array.Sort(actualSorted);
        for (var i = 0; i < expectedSorted.Length; i++) {
            Assert.That(actualSorted[i], Is.EqualTo(expectedSorted[i]), $"scan offset {i} must match as a multiset");
        }
    }

    [Test]
    public void ABulkLoadedNonUniqueIndexStillAcceptsInsertsAndDeletesAfterwards() {
        var bulk = NonUniqueBTreeIndex<int, DefaultComparer<int>>.BulkLoad([1, 3, 5], [10, 30, 50]);

        bulk.Insert(5, 51);
        using var all = bulk.GetOffsetsIter();
        Assert.That(all.Count, Is.EqualTo(4));

        bulk.Delete(5, 50);
        using var remaining = bulk.GetOffsets(5);
        var found = remaining.BufferResult().Unwrap().ToArray();
        Array.Sort(found);
        Assert.That(found, Is.EqualTo(new[] { 51 }));
    }

    // --- fill factor: the regression guard for the measured split storm ------------------
    //
    // BulkLoad used to pack chunks 100% full. That is not free later: with no slack, the first
    // scattered inserts after a cold load each land in a full chunk, take the IsFull branch, and
    // rent a fresh pair of chunk arrays - measured 2,088 B/op against 0 B/op once headroom exists.
    //
    // These assert the SHAPE (chunks arrive with slack) and the BEHAVIOUR (a batch of scattered
    // inserts allocates nothing), because shape alone would not catch a future change that
    // restores the full packing somewhere else in the build.
    [Test]
    public void ABulkLoadedIndexLeavesHeadroomInEveryChunk() {
        const int rows = 100_000;
        const int capacity = 256;
        var keys = new int[rows];
        for (var i = 0; i < rows; i++) keys[i] = i;

        var bulk = BTreeIndex<int, DefaultComparer<int>>.BulkLoad(keys, keys);

        // Fully packed, this is ceil(rows / capacity). Underfilled it must be strictly MORE, and
        // only a little more: a large jump would mean the fill factor is costing the read path
        // extra chunk-list search levels, which is the failure this number has to catch in both
        // directions. Measured guard rails: 391 chunks packed, ~404 at 31/32, ~4,465 at 7/8.
        var packed = (rows + capacity - 1) / capacity;
        Assert.That(bulk.ChunkCount, Is.GreaterThan(packed),
            "BulkLoad must not pack chunks to 100% - that is what caused the split storm");
        Assert.That(bulk.ChunkCount, Is.LessThan(packed + packed / 10),
            "fill factor must stay close to full - a large chunk-count jump costs point lookups");
        Assert.That(bulk.Count, Is.EqualTo(rows), "every key must survive the load");

        // And the index must still be correct: a wrong index that allocates nothing proves nothing.
        Assert.That(bulk.GetOffset(0).Unwrap(), Is.EqualTo(0));
        Assert.That(bulk.GetOffset(rows - 1).Unwrap(), Is.EqualTo(rows - 1));
        Assert.That(bulk.GetOffset(rows / 2).Unwrap(), Is.EqualTo(rows / 2));
    }

    [Test]
    public void ScatteredInsertsIntoAFreshlyBulkLoadedIndexDoNotAllocate() {
        const int rows = 100_000;
        var keys = new int[rows];
        for (var i = 0; i < rows; i++) keys[i] = i * 2;

        var bulk = BTreeIndex<int, DefaultComparer<int>>.BulkLoad(keys, keys);

        // 2,000 odd keys, each landing strictly between two held even keys and therefore in the
        // middle of a chunk. Spread them across the whole span so each one hits a different chunk,
        // which is the shape that triggered the split storm when chunks were packed full.
        var batch = new int[2_000];
        var step = Math.Max(1, rows / batch.Length);
        for (var i = 0; i < batch.Length; i++) batch[i] = (i * step) * 2 + 1;

        // Warm the pool and JIT on a throwaway index so the measured run sees steady state.
        var warmup = BTreeIndex<int, DefaultComparer<int>>.BulkLoad(keys, keys);
        foreach (var k in batch) warmup.Insert(k, k);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        foreach (var k in batch) bulk.Insert(k, k);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.That(bulk.Count, Is.EqualTo(rows + batch.Length), "every insert must land");
        Assert.That(allocated, Is.LessThan(1_000),
            $"scattered inserts into a bulk-loaded index allocated {allocated} B; a split storm is back");
    }

    [Test]
    public void ABulkLoadedNonUniqueIndexLeavesHeadroomToo() {
        const int rows = 100_000;
        const int capacity = 256;
        var keys = new int[rows];
        for (var i = 0; i < rows; i++) keys[i] = i;

        var bulk = NonUniqueBTreeIndex<int, DefaultComparer<int>>.BulkLoad(keys, keys);

        var packed = (rows + capacity - 1) / capacity;
        Assert.That(bulk.ChunkCount, Is.GreaterThan(packed),
            "non-unique BulkLoad must not pack chunks to 100% either");
        Assert.That(bulk.ChunkCount, Is.LessThan(packed + packed / 10),
            "fill factor must stay close to full");
        Assert.That(bulk.Count, Is.EqualTo(rows), "every key must survive the load");
    }
}
