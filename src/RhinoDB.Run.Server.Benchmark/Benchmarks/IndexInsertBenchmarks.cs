using BenchmarkDotNet.Attributes;

using RhinoDB.Lib.Indexing;

namespace RhinoDB.Run.Server.Benchmark.Benchmarks;

// Insert-throughput comparison between the two ordered index families - the
// gap IndexRangeBenchmarks.cs's own comment flags ("measures query cost, not
// insert robustness"). BTreeIndex is a flat List<IndexChunk> of fixed-
// capacity sorted arrays: a chunk split (on overflow) inserts a new chunk
// into that flat list via List.Insert - an O(numChunks) shift unless it
// happens at the tail. Sequential-order insert always splits at the tail
// (cheap, amortized O(1)); random-order insert splits scattered throughout
// the list, which is the scenario that could make BTree lose despite its
// clean range-read win. RedBlackTreeIndex (SortedSet) has no such layout
// dependency - both orderings cost the same O(log n) per insert.
[MemoryDiagnoser]
public class IndexInsertBenchmarks {
    [ParamsSource(nameof(RecordCounts))]
    public int RecordCount;

    static public IEnumerable<int> RecordCounts => BenchmarkScale.RecordCounts();

    private long[] sequentialKeys = null!;
    private long[] randomKeys = null!;

    [GlobalSetup]
    public void Setup() {
        sequentialKeys = new long[RecordCount];
        for (var i = 0; i < RecordCount; i++) sequentialKeys[i] = i;

        randomKeys = (long[])sequentialKeys.Clone();
        new Random(42).Shuffle(randomKeys);
    }

    [Benchmark(Baseline = true)]
    public int BTree_InsertSequential() {
        var index = new BTreeIndex<long>();
        foreach (var key in sequentialKeys) index.Insert(key, (int)key);
        return index.Count;
    }

    [Benchmark]
    public int RedBlack_InsertSequential() {
        var index = new RedBlackTreeIndex<long>();
        foreach (var key in sequentialKeys) index.Insert(key, (int)key);
        return index.Count;
    }

    [Benchmark]
    public int BTree_InsertRandom() {
        var index = new BTreeIndex<long>();
        foreach (var key in randomKeys) index.Insert(key, (int)key);
        return index.Count;
    }

    [Benchmark]
    public int RedBlack_InsertRandom() {
        var index = new RedBlackTreeIndex<long>();
        foreach (var key in randomKeys) index.Insert(key, (int)key);
        return index.Count;
    }
}
