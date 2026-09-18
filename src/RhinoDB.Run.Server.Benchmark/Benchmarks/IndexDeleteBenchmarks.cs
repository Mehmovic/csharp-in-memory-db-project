using BenchmarkDotNet.Attributes;

using RhinoDB.Lib.Indexing;

namespace RhinoDB.Run.Server.Benchmark.Benchmarks;

// Delete-throughput comparison, counterpart to IndexInsertBenchmarks.cs.
// BTreeIndex removes an emptied chunk from its flat List<IndexChunk> via
// List.RemoveAt - an O(numChunks) shift unless it happens at the tail.
// Sequential-order deletion always empties chunks front-to-back (a bounded,
// predictable shift pattern); random-order deletion scatters chunk removals
// across the whole list, the same structural risk IndexInsertBenchmarks.cs
// targets for inserts. Each [Benchmark] method only performs the deletes -
// the pre-populated tree it deletes from is built by a per-benchmark
// [IterationSetup], which BenchmarkDotNet excludes from the timed region.
[MemoryDiagnoser]
public class IndexDeleteBenchmarks {
    [ParamsSource(nameof(RecordCounts))]
    public int RecordCount;

    static public IEnumerable<int> RecordCounts => BenchmarkScale.RecordCounts();

    private long[] sequentialKeys = null!;
    private long[] randomDeleteOrder = null!;

    private BTreeIndex<long> bTreeSeq = null!;
    private RedBlackTreeIndex<long> redBlackSeq = null!;
    private BTreeIndex<long> bTreeRandom = null!;
    private RedBlackTreeIndex<long> redBlackRandom = null!;

    [GlobalSetup]
    public void Setup() {
        sequentialKeys = new long[RecordCount];
        for (var i = 0; i < RecordCount; i++) sequentialKeys[i] = i;

        randomDeleteOrder = (long[])sequentialKeys.Clone();
        new Random(42).Shuffle(randomDeleteOrder);
    }

    static private BTreeIndex<long> NewFilledBTree(long[] keys) {
        var index = new BTreeIndex<long>();
        foreach (var key in keys) index.Insert(key, (int)key);
        return index;
    }

    static private RedBlackTreeIndex<long> NewFilledRedBlack(long[] keys) {
        var index = new RedBlackTreeIndex<long>();
        foreach (var key in keys) index.Insert(key, (int)key);
        return index;
    }

    [IterationSetup(Target = nameof(BTree_DeleteSequential))]
    public void SetupBTreeSequential() => bTreeSeq = NewFilledBTree(sequentialKeys);

    [IterationSetup(Target = nameof(RedBlack_DeleteSequential))]
    public void SetupRedBlackSequential() => redBlackSeq = NewFilledRedBlack(sequentialKeys);

    [IterationSetup(Target = nameof(BTree_DeleteRandom))]
    public void SetupBTreeRandom() => bTreeRandom = NewFilledBTree(sequentialKeys);

    [IterationSetup(Target = nameof(RedBlack_DeleteRandom))]
    public void SetupRedBlackRandom() => redBlackRandom = NewFilledRedBlack(sequentialKeys);

    [Benchmark(Baseline = true)]
    public int BTree_DeleteSequential() {
        foreach (var key in sequentialKeys) bTreeSeq.Delete(key);
        return bTreeSeq.Count;
    }

    [Benchmark]
    public int RedBlack_DeleteSequential() {
        foreach (var key in sequentialKeys) redBlackSeq.Delete(key);
        return redBlackSeq.Count;
    }

    [Benchmark]
    public int BTree_DeleteRandom() {
        foreach (var key in randomDeleteOrder) bTreeRandom.Delete(key);
        return bTreeRandom.Count;
    }

    [Benchmark]
    public int RedBlack_DeleteRandom() {
        foreach (var key in randomDeleteOrder) redBlackRandom.Delete(key);
        return redBlackRandom.Count;
    }
}
