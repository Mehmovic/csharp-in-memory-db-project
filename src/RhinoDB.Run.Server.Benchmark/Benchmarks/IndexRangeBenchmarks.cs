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

        // Center the query window so both indexes do the same amount of work.
        from = KeyCount / 2 - RangeWidth / 2;

        // Guard: a Range that returns the wrong window would time garbage instead
        // of failing, and this index is ~390 chunks deep at 100k keys - far beyond
        // what the unit tests exercise.
        using var bTreeResult = bTree.GetOffsetsRange(from, from + RangeWidth - 1);
        if (bTreeResult.Count != RangeWidth)
            throw new InvalidOperationException($"LiteBTree_Range returned {bTreeResult.Count} offsets, expected {RangeWidth}.");


        using var redBlackResult = redBlack.GetOffsetsRange(from, from + RangeWidth - 1);
        if (redBlackResult.Count != RangeWidth)
            throw new InvalidOperationException($"RedBlack_Range returned {redBlackResult.Count} offsets, expected {RangeWidth}.");
    }

    [Benchmark(Baseline = true)]
    public int LiteBTree_Range() {
        using var res = bTree.GetOffsetsRange(from, from + RangeWidth - 1);
        return res.Count;
    }

    [Benchmark]
    public int RedBlack_Range() {
        using var res = redBlack.GetOffsetsRange(from, from + RangeWidth - 1);
        return res.Count;
    }
}
