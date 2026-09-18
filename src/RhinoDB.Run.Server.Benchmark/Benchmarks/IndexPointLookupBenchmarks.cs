using BenchmarkDotNet.Attributes;

using RhinoDB.Lib.Indexing;

namespace RhinoDB.Run.Server.Benchmark.Benchmarks;

// Single-key lookup comparison - IndexRangeBenchmarks.cs only measures range
// scans, never a plain GetOffset. Keys are inserted sequentially (the
// realistic steady state for an ascending-PK table) so this isolates lookup
// cost, not insert layout.
[MemoryDiagnoser]
public class IndexPointLookupBenchmarks {
    private const int KeyCount = 100_000;

    private BTreeIndex<long> bTree = null!;
    private RedBlackTreeIndex<long> redBlack = null!;
    private long lookupKey;

    [GlobalSetup]
    public void Setup() {
        bTree = new BTreeIndex<long>();
        redBlack = new RedBlackTreeIndex<long>();
        for (var key = 0L; key < KeyCount; key++) {
            bTree.Insert(key, (int)key);
            redBlack.Insert(key, (int)key);
        }

        lookupKey = KeyCount / 2;

        // Guard: a wrong fixture key would time a not-found error path instead of
        // a real lookup for both benchmarks.
        if (bTree.GetOffset(lookupKey).IsError())
            throw new InvalidOperationException("BTree_PointLookup: lookup key not found - fixture is broken.");
        if (redBlack.GetOffset(lookupKey).IsError())
            throw new InvalidOperationException("RedBlack_PointLookup: lookup key not found - fixture is broken.");
    }

    [Benchmark(Baseline = true)]
    public int BTree_PointLookup() => bTree.GetOffset(lookupKey).Unwrap();

    [Benchmark]
    public int RedBlack_PointLookup() => redBlack.GetOffset(lookupKey).Unwrap();
}
