using BenchmarkDotNet.Attributes;

using RhinoDB.Core;
using RhinoDB.Lib.Indexing;

namespace RhinoDB.Sandbox.Benchmark.Benchmarks;

// Three questions.
//
// 1. Does the unique HashIndex beat the unique BTreeIndex on a point lookup? The non-unique
//    run says hash wins by 1.58x, and both are Dictionary + a bucket walk, so the unique pair
//    should behave the same - but it was never measured. If it holds, then BTreeIndex earns
//    its keep for ordering only, never for lookup speed, which is the kind of statement that
//    should decide how tables are declared.
//
// 2. Is there a scaling wall? Splitting inserts a chunk with chunks.Insert(idx + 1, ...),
//    which is O(chunk count) on a List<IndexChunk>. Invisible at 100k rows, potentially a
//    ~156 KB memmove per split at 10M rows.
//
// 3. Does random insertion order actually pay it? Sequential load always splits at the LAST
//    chunk, so Insert is an append and the wall never shows. That is the trap the previous
//    run fell into: flat ns/row from 1M to 10M proved nothing, because it measured the one
//    order that cannot trigger the cost. The permutations below are built once in GlobalSetup
//    so the timed method measures index inserts only, never the shuffle.
//
// Read cost per row, not the absolute time. If RandomLoad ns/row climbs away from Load ns/row
// as rows grow, the chunk-list insert is real and not just arithmetic on paper.
//
// Every index is asserted in GlobalSetup before timing, so a structurally wrong index cannot
// time perfectly while returning the wrong window.
[MemoryDiagnoser]
public class IndexScalingBenchmarks {
    private const int Rows = 100_000;
    private const int Mid = 1_000_000;
    private const int Large = 4_000_000;
    private const int Huge = 10_000_000;

    private HashIndex<int> uniqueHash = null!;
    private BTreeIndex<int, DefaultComparer<int>> uniqueBTree = null!;
    private HashIndex<string> uniqueHashString = null!;
    private BTreeIndex<string, OrdinalStringComparer> uniqueBTreeString = null!;

    private int[] order1M = null!;
    private int[] order4M = null!;
    private int[] order10M = null!;

    [GlobalSetup]
    public void Setup() {
        uniqueHash = new HashIndex<int>();
        uniqueBTree = new BTreeIndex<int, DefaultComparer<int>>();
        uniqueHashString = new HashIndex<string>();
        uniqueBTreeString = new(256);

        for (var i = 0; i < Rows; i++) {
            uniqueHash.Insert(i, i);
            uniqueBTree.Insert(i, i);
            uniqueHashString.Insert(Key(i), i);
            uniqueBTreeString.Insert(Key(i), i);
        }

        order1M = Shuffled(Mid, 1);
        order4M = Shuffled(Large, 2);
        order10M = Shuffled(Huge, 3);

        Assert(uniqueHash.GetOffset(Rows / 2).IsOk(), "unique hash point must resolve");
        Assert(uniqueBTree.GetOffset(Rows / 2).IsOk(), "unique btree point must resolve");
        Assert(uniqueHashString.GetOffset(Key(Rows / 2)).IsOk(), "unique hash string point must resolve");
        Assert(uniqueBTreeString.GetOffset(Key(Rows / 2)).IsOk(), "unique btree string point must resolve");
        Assert(Count(uniqueHash.GetOffsetsIter()) == Rows, "unique hash scan must return every offset");
        Assert(Count(uniqueBTree.GetOffsetsIter()) == Rows, "unique btree scan must return every offset");

        // A randomly built index must still be a working index, not just a fast one. Load a
        // scrambled 200k set and prove every key resolves - a split ordering bug would show up
        // here as a missing or wrong offset, which is exactly what the wall question is near.
        var probe = new BTreeIndex<int, DefaultComparer<int>>();
        foreach (var i in Shuffled(200_000, 99)) probe.Insert(i, i);
        for (var i = 0; i < 200_000; i += 997) {
            Assert(probe.GetOffset(i).Unwrap() == i, "randomly built index must resolve every key");
        }
    }

    // xorshift32 + Fisher-Yates. Deliberately not System.Random: this has to be identical on
    // every runtime so the benchmark measures the index rather than the shuffle.
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

    static private string Key(int i) => "player-" + i.ToString("D9");

    static private int Count(StackArrayPoolContainer<int> container) {
        using (container) return container.Count;
    }

    static private void Assert(bool condition, string message) {
        if (!condition) throw new InvalidOperationException("index is structurally wrong: " + message);
    }

    // --- unique point lookup: hash vs btree, same scale ----------------------------

    [Benchmark(Baseline = true)]
    public int UniqueLookup_BTree_Int() => uniqueBTree.GetOffset(Rows / 2).Unwrap();

    [Benchmark]
    public int UniqueLookup_Hash_Int() => uniqueHash.GetOffset(Rows / 2).Unwrap();

    [Benchmark]
    public int UniqueLookup_BTree_String() => uniqueBTreeString.GetOffset(Key(Rows / 2)).Unwrap();

    [Benchmark]
    public int UniqueLookup_Hash_String() => uniqueHashString.GetOffset(Key(Rows / 2)).Unwrap();

    // --- sequential load: the baseline, and the case that hides the wall ------------

    [Benchmark]
    public int Scale_Load_1M_BTree() => LoadBTree(Mid);

    [Benchmark]
    public int Scale_Load_4M_BTree() => LoadBTree(Large);

    [Benchmark]
    public int Scale_Load_10M_BTree() => LoadBTree(Huge);

    [Benchmark]
    public int Scale_Load_1M_Hash() => LoadHash(Mid);

    [Benchmark]
    public int Scale_Load_10M_Hash() => LoadHash(Huge);

    // --- random load: splits land throughout the chunk list, so chunks.Insert cannot
    // --- degenerate into an append. This is the case that actually tests the wall. -----

    [Benchmark]
    public int Scale_RandomLoad_1M_BTree() {
        var index = new BTreeIndex<int, DefaultComparer<int>>();
        foreach (var i in order1M) index.Insert(i, i);
        return index.Count;
    }

    [Benchmark]
    public int Scale_RandomLoad_4M_BTree() {
        var index = new BTreeIndex<int, DefaultComparer<int>>();
        foreach (var i in order4M) index.Insert(i, i);
        return index.Count;
    }

    [Benchmark]
    public int Scale_RandomLoad_10M_BTree() {
        var index = new BTreeIndex<int, DefaultComparer<int>>();
        foreach (var i in order10M) index.Insert(i, i);
        return index.Count;
    }

    // Hash has no chunk list, so it is the control: if random load is flat for hash and
    // rising for BTree, the difference is the List<IndexChunk> insert and nothing else.
    [Benchmark]
    public int Scale_RandomLoad_1M_Hash() {
        var index = new HashIndex<int>();
        foreach (var i in order1M) index.Insert(i, i);
        return index.Count;
    }

    [Benchmark]
    public int Scale_RandomLoad_10M_Hash() {
        var index = new HashIndex<int>();
        foreach (var i in order10M) index.Insert(i, i);
        return index.Count;
    }

    static private int LoadBTree(int rows) {
        var index = new BTreeIndex<int, DefaultComparer<int>>();
        for (var i = 0; i < rows; i++) index.Insert(i, i);
        return index.Count;
    }

    static private int LoadHash(int rows) {
        var index = new HashIndex<int>();
        for (var i = 0; i < rows; i++) index.Insert(i, i);
        return index.Count;
    }
    // BulkLoad builds the whole index with one sort plus a linear pass, so nothing splits and
    // nothing shifts - the thing random-order Insert cannot avoid. Compare it against random-order
    // Insert at the same scales; if BulkLoad lands near the sequential numbers, the cliff is gone.
    [Benchmark]
    public int Scale_BulkLoad_1M_BTree() => BulkBTree(order1M);

    [Benchmark]
    public int Scale_BulkLoad_4M_BTree() => BulkBTree(order4M);

    [Benchmark]
    public int Scale_BulkLoad_10M_BTree() => BulkBTree(order10M);

    [Benchmark]
    public int Scale_BulkLoad_10M_NonUniqueBTree() => BulkNonUnique(order10M);

    static private int BulkBTree(int[] order) =>
        BTreeIndex<int, DefaultComparer<int>>.BulkLoad(order, order).Count;

    static private int BulkNonUnique(int[] order) =>
        NonUniqueBTreeIndex<int, DefaultComparer<int>>.BulkLoad(order, order).Count;
}
