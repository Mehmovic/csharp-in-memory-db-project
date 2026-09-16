using BenchmarkDotNet.Attributes;
using RhinoDB.Lib.Indexing;

namespace RhinoDB.Run.Server.Benchmark.Benchmarks;

// Range-query comparison between the two ordered index families at equal shape:
// 100k sequential keys, identical offsets, one shared reused fill buffer.
// Keys are inserted sequentially (LiteBTree's ideal layout - full, contiguous
// chunks), so this measures query cost, not insert robustness.
// Heap (B/op) shows each index's own internal allocation: with a caller-owned
// fill buffer, a well-behaved Range should be ~0 B/op.
[MemoryDiagnoser]
public class IndexRangeBenchmarks {
    private const int KeyCount = 100_000;

    private BTreeIndex<long> bTree = null!;
    private RedBlackTreeIndex<long> redBlack = null!;
    private List<int> buffer = null!;
    private long from;

    [Params(1, 100, 10_000)]
    public int RangeWidth;

    [GlobalSetup]
    public void Setup() {
        bTree = new BTreeIndex<long>();
        redBlack = new RedBlackTreeIndex<long>();
        for (var key = 0L; key < KeyCount; key++) {
            bTree.Insert(key, (int)key);
            redBlack.Insert(key, (int)key);
        }

        buffer = [];
        // Center the query window so both indexes do the same amount of work.
        from = KeyCount / 2 - RangeWidth / 2;

        // Guard: a Range that returns the wrong window would time garbage instead
        // of failing, and this index is ~390 chunks deep at 100k keys - far beyond
        // what the unit tests exercise.
        buffer.Clear();
        bTree.Range(from, from + RangeWidth - 1, buffer);
        if (buffer.Count != RangeWidth)
            throw new InvalidOperationException($"LiteBTree_Range returned {buffer.Count} offsets, expected {RangeWidth}.");

        buffer.Clear();
        redBlack.Range(from, from + RangeWidth - 1, buffer);
        if (buffer.Count != RangeWidth)
            throw new InvalidOperationException($"RedBlack_Range returned {buffer.Count} offsets, expected {RangeWidth}.");

        buffer.Clear();
    }

    [Benchmark(Baseline = true)]
    public int LiteBTree_Range() {
        buffer.Clear();
        bTree.Range(from, from + RangeWidth - 1, buffer);
        return buffer.Count;
    }

    [Benchmark]
    public int RedBlack_Range() {
        buffer.Clear();
        redBlack.Range(from, from + RangeWidth - 1, buffer);
        return buffer.Count;
    }
}