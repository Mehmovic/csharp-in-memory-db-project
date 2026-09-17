using RhinoDB.Core;

namespace RhinoDB.Lib.Indexing.Test;

// The Gt/Gte/Lt/Lte/Iter surface lives on the shared OrderedIndex<TKey> base, so its
// bound mapping is verified once against every ordered implementation rather than
// copy-pasted per index. Every case goes through the public wrappers only, the same
// way production callers use them.
//
// The scans return pooled OffsetWriters - ref structs, which cannot flow through
// dynamic - so each implementation is adapted with statically typed delegates and
// materialized to a plain array before asserting.
public class OrderedIndexTests {
    private sealed class Impl {
        public required string Name { get; init; }
        public required bool AllowsDuplicateKeys { get; init; }
        public required dynamic Create { get; init; }
        public required dynamic Gt { get; init; }
        public required dynamic Gte { get; init; }
        public required dynamic Lt { get; init; }
        public required dynamic Lte { get; init; }
        public required dynamic Iter { get; init; }
        public required dynamic Range { get; init; }
    }

    static private int[] Read(OffsetList writer) {
        using var w = writer;
        return w.Buffer().Unwrap().ToArray();
    }

    static private readonly Impl[] All = [
        new() {
            Name = "BTreeIndex", AllowsDuplicateKeys = false,
            Create = (Func<BTreeIndex<int>>)(() => new BTreeIndex<int>()),
            Gt = (Func<BTreeIndex<int>, int, int[]>)((i, k) => Read(i.Gt(k))),
            Gte = (Func<BTreeIndex<int>, int, int[]>)((i, k) => Read(i.Gte(k))),
            Lt = (Func<BTreeIndex<int>, int, int[]>)((i, k) => Read(i.Lt(k))),
            Lte = (Func<BTreeIndex<int>, int, int[]>)((i, k) => Read(i.Lte(k))),
            Iter = (Func<BTreeIndex<int>, int[]>)((i) => Read(i.Iter())),
            Range = (Func<BTreeIndex<int>, int, int, int[]>)((i, f, t) => Read(i.Range(f, t))),
        },
        new() {
            Name = "NonUniqueBTreeIndex", AllowsDuplicateKeys = true,
            Create = (Func<NonUniqueBTreeIndex<int>>)(() => new NonUniqueBTreeIndex<int>()),
            Gt = (Func<NonUniqueBTreeIndex<int>, int, int[]>)((i, k) => Read(i.Gt(k))),
            Gte = (Func<NonUniqueBTreeIndex<int>, int, int[]>)((i, k) => Read(i.Gte(k))),
            Lt = (Func<NonUniqueBTreeIndex<int>, int, int[]>)((i, k) => Read(i.Lt(k))),
            Lte = (Func<NonUniqueBTreeIndex<int>, int, int[]>)((i, k) => Read(i.Lte(k))),
            Iter = (Func<NonUniqueBTreeIndex<int>, int[]>)((i) => Read(i.Iter())),
            Range = (Func<NonUniqueBTreeIndex<int>, int, int, int[]>)((i, f, t) => Read(i.Range(f, t))),
        },
        new() {
            Name = "RedBlackTreeIndex", AllowsDuplicateKeys = false,
            Create = (Func<RedBlackTreeIndex<int>>)(() => new RedBlackTreeIndex<int>()),
            Gt = (Func<RedBlackTreeIndex<int>, int, int[]>)((i, k) => Read(i.Gt(k))),
            Gte = (Func<RedBlackTreeIndex<int>, int, int[]>)((i, k) => Read(i.Gte(k))),
            Lt = (Func<RedBlackTreeIndex<int>, int, int[]>)((i, k) => Read(i.Lt(k))),
            Lte = (Func<RedBlackTreeIndex<int>, int, int[]>)((i, k) => Read(i.Lte(k))),
            Iter = (Func<RedBlackTreeIndex<int>, int[]>)((i) => Read(i.Iter())),
            Range = (Func<RedBlackTreeIndex<int>, int, int, int[]>)((i, f, t) => Read(i.Range(f, t))),
        },
        new() {
            Name = "NonUniqueRedBlackTreeIndex", AllowsDuplicateKeys = true,
            Create = (Func<NonUniqueRedBlackTreeIndex<int>>)(() => new NonUniqueRedBlackTreeIndex<int>()),
            Gt = (Func<NonUniqueRedBlackTreeIndex<int>, int, int[]>)((i, k) => Read(i.Gt(k))),
            Gte = (Func<NonUniqueRedBlackTreeIndex<int>, int, int[]>)((i, k) => Read(i.Gte(k))),
            Lt = (Func<NonUniqueRedBlackTreeIndex<int>, int, int[]>)((i, k) => Read(i.Lt(k))),
            Lte = (Func<NonUniqueRedBlackTreeIndex<int>, int, int[]>)((i, k) => Read(i.Lte(k))),
            Iter = (Func<NonUniqueRedBlackTreeIndex<int>, int[]>)((i) => Read(i.Iter())),
            Range = (Func<NonUniqueRedBlackTreeIndex<int>, int, int, int[]>)((i, f, t) => Read(i.Range(f, t))),
        },
    ];

    static private readonly Impl[] NonUniqueOnly = [.. All.Where(i => i.AllowsDuplicateKeys)];

    [Test]
    public void Gte_IncludesTheBoundKey_AndEverythingAbove() {
        foreach (var impl in All) {
            dynamic index = impl.Create();
            index.Insert(1, 10);
            index.Insert(2, 20);
            index.Insert(3, 30);

            Assert.That(impl.Gte(index, 2), Is.EqualTo(new[] { 20, 30 }), impl.Name);
        }
    }

    [Test]
    public void Gt_ExcludesTheBoundKey_ButIncludesEverythingAbove() {
        foreach (var impl in All) {
            dynamic index = impl.Create();
            index.Insert(1, 10);
            index.Insert(2, 20);
            index.Insert(3, 30);

            Assert.That(impl.Gt(index, 2), Is.EqualTo(new[] { 30 }), impl.Name);
        }
    }

    [Test]
    public void Lte_IncludesTheBoundKey_AndEverythingBelow() {
        foreach (var impl in All) {
            dynamic index = impl.Create();
            index.Insert(1, 10);
            index.Insert(2, 20);
            index.Insert(3, 30);

            Assert.That(impl.Lte(index, 2), Is.EqualTo(new[] { 10, 20 }), impl.Name);
        }
    }

    [Test]
    public void Lt_ExcludesTheBoundKey_ButIncludesEverythingBelow() {
        foreach (var impl in All) {
            dynamic index = impl.Create();
            index.Insert(1, 10);
            index.Insert(2, 20);
            index.Insert(3, 30);

            Assert.That(impl.Lt(index, 2), Is.EqualTo(new[] { 10 }), impl.Name);
        }
    }

    [Test]
    public void Iter_ReturnsEveryOffset_InAscendingKeyOrder() {
        foreach (var impl in All) {
            dynamic index = impl.Create();
            index.Insert(3, 30);
            index.Insert(1, 10);
            index.Insert(2, 20);

            Assert.That(impl.Iter(index), Is.EqualTo(new[] { 10, 20, 30 }), impl.Name);
        }
    }

    [Test]
    public void Gte_FromAboveEveryKey_ReturnsZeroOffsets() {
        // Regression guard: SortedSet.GetViewBetween throws when its lower bound sits
        // above the largest key - both RedBlack scans must return empty instead.
        foreach (var impl in All) {
            dynamic index = impl.Create();
            index.Insert(1, 10);
            index.Insert(2, 20);

            Assert.That(impl.Gte(index, 99), Is.Empty, impl.Name);
        }
    }

    [Test]
    public void Gt_FromTheLargestKey_ReturnsZeroOffsets() {
        foreach (var impl in All) {
            dynamic index = impl.Create();
            index.Insert(1, 10);
            index.Insert(2, 20);

            Assert.That(impl.Gt(index, 2), Is.Empty, impl.Name);
        }
    }

    [Test]
    public void EveryOpenEndedMethod_OnAnEmptyIndex_ReturnsZeroWithoutThrowing() {
        foreach (var impl in All) {
            dynamic index = impl.Create();

            Assert.That(impl.Gt(index, 1), Is.Empty, impl.Name);
            Assert.That(impl.Gte(index, 1), Is.Empty, impl.Name);
            Assert.That(impl.Lt(index, 1), Is.Empty, impl.Name);
            Assert.That(impl.Lte(index, 1), Is.Empty, impl.Name);
            Assert.That(impl.Iter(index), Is.Empty, impl.Name);
        }
    }

    [Test]
    public void Range_WithAnInvertedSpan_ReturnsZero() {
        foreach (var impl in All) {
            dynamic index = impl.Create();
            index.Insert(1, 10);
            index.Insert(2, 20);
            index.Insert(3, 30);

            Assert.That(impl.Range(index, 3, 1), Is.Empty, impl.Name);
        }
    }

    [Test]
    public void Gt_WithADuplicateRunAtTheBound_SkipsEveryDuplicate() {
        foreach (var impl in NonUniqueOnly) {
            dynamic index = impl.Create();
            index.Insert(5, 50);
            index.Insert(5, 51);
            index.Insert(5, 52);
            index.Insert(9, 90);

            Assert.That(impl.Gt(index, 5), Is.EqualTo(new[] { 90 }), impl.Name);
            // Within a chunk duplicates keep insertion order, across chunks they may
            // not - so a run of three plus the later key asserts set-equality.
            Assert.That(impl.Gte(index, 5), Is.EquivalentTo(new[] { 50, 51, 52, 90 }), impl.Name);
        }
    }

    [Test]
    public void Lt_WithADuplicateRunAtTheBound_ExcludesEveryDuplicate() {
        foreach (var impl in NonUniqueOnly) {
            dynamic index = impl.Create();
            index.Insert(1, 10);
            index.Insert(5, 50);
            index.Insert(5, 51);
            index.Insert(5, 52);

            Assert.That(impl.Lt(index, 5), Is.EqualTo(new[] { 10 }), impl.Name);
            Assert.That(impl.Lte(index, 5), Is.EquivalentTo(new[] { 10, 50, 51, 52 }), impl.Name);
        }
    }
}
