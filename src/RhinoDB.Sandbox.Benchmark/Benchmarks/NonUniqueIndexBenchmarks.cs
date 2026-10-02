using BenchmarkDotNet.Attributes;

using RhinoDB.Core;
using RhinoDB.Lib.Indexing;

namespace RhinoDB.Sandbox.Benchmark.Benchmarks;

// Non-unique indexes are the ones a game is most likely to reach for: a low-cardinality
// secondary column (Region, Position, Status) is almost never unique. Until now only the
// unique BTree had a performance baseline, so the duplicate-run walk - the code that only
// executes when a key repeats, including across a chunk boundary - had never been timed.
//
// WideDuplicateRun is the case that matters most. 500 offsets per key against a 256-entry
// chunk means every run straddles a boundary, so an index that only walked its own chunk
// would silently return 256 of 500 and still look healthy on every other benchmark. That is
// the exact bug class the cross-chunk tests catch, now with a cost attached.
//
// Every index is asserted in GlobalSetup before any timing runs: a structurally wrong index
// used to time perfectly while returning the wrong window.
[MemoryDiagnoser]
public class NonUniqueIndexBenchmarks {
    private const int ChunkSize = 256;

    // Low cardinality: 1000 distinct keys, 50 offsets each. The shape a Region or Position
    // index actually has - many rows, few distinct values.
    private const int LowKeys = 1_000;
    private const int LowPerKey = 50;
    private const int LowTotal = LowKeys * LowPerKey;

    // Wide duplicate run: 200 keys, 500 offsets each. 500 > ChunkSize, so every run crosses
    // at least one chunk boundary.
    private const int WideKeys = 200;
    private const int WidePerKey = 500;
    private const int WideTotal = WideKeys * WidePerKey;

    private const int HighRows = 100_000;

    private const int LowProbeKey = LowKeys / 2;
    private const int WideProbeKey = WideKeys / 2;

    private NonUniqueBTreeIndex<int> lowBTree = null!;
    private NonUniqueBTreeIndex<int> wideBTree = null!;
    private NonUniqueBTreeIndex<int> highBTree = null!;
    private NonUniqueBTreeIndex<string> stringBTree = null!;

    private NonUniqueHashIndex<int> lowHash = null!;
    private NonUniqueHashIndex<int> wideHash = null!;
    private NonUniqueHashIndex<int> highHash = null!;

    [GlobalSetup]
    public void Setup() {
        lowBTree = new NonUniqueBTreeIndex<int>(ChunkSize);
        wideBTree = new NonUniqueBTreeIndex<int>(ChunkSize);
        highBTree = new NonUniqueBTreeIndex<int>(ChunkSize);
        stringBTree = new NonUniqueBTreeIndex<string>(ChunkSize, StringComparer.Ordinal);

        lowHash = new NonUniqueHashIndex<int>();
        wideHash = new NonUniqueHashIndex<int>();
        highHash = new NonUniqueHashIndex<int>();

        for (var k = 0; k < LowKeys; k++) {
            for (var o = 0; o < LowPerKey; o++) {
                var offset = k * LowPerKey + o;
                lowBTree.Insert(k, offset);
                lowHash.Insert(k, offset);
                stringBTree.Insert(StrKey(k), offset);
            }
        }

        for (var k = 0; k < WideKeys; k++) {
            for (var o = 0; o < WidePerKey; o++) {
                var offset = k * WidePerKey + o;
                wideBTree.Insert(k, offset);
                wideHash.Insert(k, offset);
            }
        }

        for (var i = 0; i < HighRows; i++) {
            highBTree.Insert(i, i);
            highHash.Insert(i, i);
        }

        Assert(Count(lowBTree.GetOffsets(LowProbeKey)) == LowPerKey, "low-cardinality BTree point must return every offset");
        Assert(Count(wideBTree.GetOffsets(WideProbeKey)) == WidePerKey, "wide-run BTree must cross chunk boundaries intact");
        Assert(Count(highBTree.GetOffsets(HighRows / 2)) == 1, "high-cardinality BTree point must return one offset");
        Assert(Count(lowHash.GetOffsets(LowProbeKey)) == LowPerKey, "low-cardinality hash point must return every offset");
        Assert(Count(wideHash.GetOffsets(WideProbeKey)) == WidePerKey, "wide-run hash must return every offset");
        Assert(Count(highHash.GetOffsets(HighRows / 2)) == 1, "high-cardinality hash point must return one offset");
        Assert(Count(stringBTree.GetOffsets(StrKey(LowProbeKey))) == LowPerKey, "string BTree must return every offset for the run");

        Assert(Count(lowBTree.GetOffsetsIter()) == LowTotal, "full scan must return every low-cardinality offset");
        Assert(Count(wideBTree.GetOffsetsIter()) == WideTotal, "full scan must return every wide-run offset");
        Assert(Count(lowBTree.GetOffsetsExcept(LowProbeKey)) == LowTotal - LowPerKey, "except must drop exactly the excluded run");
        Assert(Count(lowBTree.GetOffsetsRange(LowProbeKey, LowProbeKey + 99)) == 100 * LowPerKey, "a 100-key range must include every duplicate");

        // One offset removed from a 500-wide run must leave the other 499 reachable, and must
        // not disturb the neighbours sharing that chunk.
        wideBTree.Delete(WideProbeKey, WideProbeKey * WidePerKey);
        Assert(Count(wideBTree.GetOffsets(WideProbeKey)) == WidePerKey - 1, "deleting one offset of a wide run must leave the rest");
        Assert(Count(wideBTree.GetOffsetsIter()) == WideTotal - 1, "a partial delete must not lose a sibling offset");
        wideBTree.Insert(WideProbeKey, WideProbeKey * WidePerKey);
        Assert(Count(wideBTree.GetOffsets(WideProbeKey)) == WidePerKey, "reinserting must restore the run");
    }

    static private string StrKey(int i) => "region-" + i.ToString("D6");

    static private int Count(StackArrayPoolContainer<int> container) {
        using (container) return container.Count;
    }

    static private void Assert(bool condition, string message) {
        if (!condition) throw new InvalidOperationException("index is structurally wrong: " + message);
    }

    // --- point lookups -------------------------------------------------------------

    [Benchmark(Baseline = true)]
    public int BTreeLookup_LowCardinality_50PerKey() => Count(lowBTree.GetOffsets(LowProbeKey));

    [Benchmark]
    public int BTreeLookup_WideDuplicateRun_500PerKey() => Count(wideBTree.GetOffsets(WideProbeKey));

    [Benchmark]
    public int BTreeLookup_HighCardinality_1PerKey() => Count(highBTree.GetOffsets(HighRows / 2));

    [Benchmark]
    public int BTreeLookup_String_LowCardinality() => Count(stringBTree.GetOffsets(StrKey(LowProbeKey)));

    [Benchmark]
    public int HashLookup_LowCardinality_50PerKey() => Count(lowHash.GetOffsets(LowProbeKey));

    [Benchmark]
    public int HashLookup_WideDuplicateRun_500PerKey() => Count(wideHash.GetOffsets(WideProbeKey));

    [Benchmark]
    public int HashLookup_HighCardinality_1PerKey() => Count(highHash.GetOffsets(HighRows / 2));

    // --- ordered reads, BTree only (hash has no range) ------------------------------

    [Benchmark]
    public int BTreeRange_OneKey_LowCardinality() => Count(lowBTree.GetOffsetsRange(LowProbeKey, LowProbeKey));

    [Benchmark]
    public int BTreeRange_100Keys_LowCardinality() => Count(lowBTree.GetOffsetsRange(LowProbeKey, LowProbeKey + 99));

    [Benchmark]
    public int BTreeGt_LowCardinality() => Count(lowBTree.GetOffsetsGt(LowProbeKey));

    // --- the generator Iter()/Except() path ----------------------------------------

    [Benchmark]
    public int BTreeScan_All_LowCardinality() => Count(lowBTree.GetOffsetsIter());

    [Benchmark]
    public int BTreeScan_All_WideDuplicateRun() => Count(wideBTree.GetOffsetsIter());

    [Benchmark]
    public int BTreeScan_Except_LowCardinality() => Count(lowBTree.GetOffsetsExcept(LowProbeKey));

    [Benchmark]
    public int HashScan_All_LowCardinality() => Count(lowHash.GetOffsetsIter());

    [Benchmark]
    public int HashScan_All_WideDuplicateRun() => Count(wideHash.GetOffsetsIter());

    [Benchmark]
    public int HashScan_Except_LowCardinality() => Count(lowHash.GetOffsetsExcept(LowProbeKey));

    // --- writes: dominated by chunk splits, so this is chunk allocation cost ----------

    [Benchmark]
    public int BTreeBulkLoad_LowCardinality() {
        var index = new NonUniqueBTreeIndex<int>(ChunkSize);
        for (var k = 0; k < LowKeys; k++)
            for (var o = 0; o < LowPerKey; o++)
                index.Insert(k, k * LowPerKey + o);
        return index.Count;
    }

    [Benchmark]
    public int BTreeBulkLoad_WideDuplicateRun() {
        var index = new NonUniqueBTreeIndex<int>(ChunkSize);
        for (var k = 0; k < WideKeys; k++)
            for (var o = 0; o < WidePerKey; o++)
                index.Insert(k, k * WidePerKey + o);
        return index.Count;
    }

    [Benchmark]
    public int HashBulkLoad_WideDuplicateRun() {
        var index = new NonUniqueHashIndex<int>();
        for (var k = 0; k < WideKeys; k++)
            for (var o = 0; o < WidePerKey; o++)
                index.Insert(k, k * WidePerKey + o);
        return index.Count;
    }

    // --- delete from inside a wide run: the offset is not the last one, so this is not a
    // --- tail pop. It exercises the in-run compaction and its sibling relocations. -----

    [Benchmark]
    public int BTreeDeleteOneOfMany_WideRun() {
        var index = new NonUniqueBTreeIndex<int>(ChunkSize);
        for (var k = 0; k < WideKeys; k++)
            for (var o = 0; o < WidePerKey; o++)
                index.Insert(k, k * WidePerKey + o);
        index.Delete(WideProbeKey, WideProbeKey * WidePerKey + 1);
        return Count(index.GetOffsets(WideProbeKey));
    }

    [Benchmark]
    public int HashDeleteOneOfMany_WideRun() {
        var index = new NonUniqueHashIndex<int>();
        for (var k = 0; k < WideKeys; k++)
            for (var o = 0; o < WidePerKey; o++)
                index.Insert(k, k * WidePerKey + o);
        index.Delete(WideProbeKey, WideProbeKey * WidePerKey + 1);
        return Count(index.GetOffsets(WideProbeKey));
    }
}