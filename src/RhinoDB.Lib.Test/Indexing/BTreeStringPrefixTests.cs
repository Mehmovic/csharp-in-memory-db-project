using RhinoDB.Core;

namespace RhinoDB.Lib.Indexing.Test;

// String indexes on OrdinalStringComparer keep an "anchor" (the prefix every key shares) and a
// packed 4-char integer prefix per key taken right after it, so most search steps compare
// integers instead of dereferencing strings. That is only correct while three things hold:
//
//   * the anchor really is shared by every key - it must shrink (and every packed prefix be
//     rebuilt) the moment a key that diverges earlier is inserted;
//   * a probe that does not share the anchor is classified as below/above every key, including
//     probes that are a PREFIX of the anchor, null, or empty;
//   * the packed prefix preserves ordinal order - unsigned 16-bit code units, a shorter string
//     sorting first, '\0' and code units >= 0x8000 included.
//
// Every test here is differential against a sorted list ordered by string.CompareOrdinal (which,
// unlike SortedDictionary, also orders null), and the key families are chosen to break each of
// those invariants if the implementation gets it wrong.
public class BTreeStringPrefixTests {
    private const int ChunkSize = 16;

    private struct XorShift(uint seed) {
        private uint state = seed | 1u;

        public int Next(int maxExclusive) {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return (int)(state % (uint)maxExclusive);
        }
    }

    static private readonly Comparison<(string? Key, int Offset)> ByKeyThenOffset = (a, b) => {
        var c = string.CompareOrdinal(a.Key, b.Key);
        return c != 0 ? c : a.Offset.CompareTo(b.Offset);
    };

    static private int[] Read(StackArrayPoolContainer<int> container) {
        using var c = container;
        return c.Buffer().ToArray();
    }

    static private int[] Where(List<(string? Key, int Offset)> model, Func<string?, bool> keep)
        => model.Where(e => keep(e.Key)).Select(e => e.Offset).ToArray();

    // Long shared prefix, keys that diverge only late, and the edge keys that probe ordering.
    static private string? KeyOf(int n, int family) => family switch {
        0 => "player-" + n.ToString("D9"),
        1 => n % 50 == 0 ? "club-" + n : "player-" + n.ToString("D9"),
        2 => (n % 7) switch {
            0 => "player-",
            1 => "player-" + n,
            2 => "player-\0" + n,
            3 => "player-" + (char)(0xFFF0 + n % 15) + n,
            4 => "pl",
            5 => "player-00" + n,
            _ => "player-" + n.ToString("D9"),
        },
        _ => n % 97 == 0 ? null : n % 89 == 0 ? "" : "player-" + n.ToString("D6"),
    };

    static private string?[] Probes(int family, int keySpace) => [
        null, "", "p", "pl", "player", "player-", "player-\0", "player-0", "player-1",
        "player-999999999999", "player-￿", "q", "a", "club-", "club-50",
        KeyOf(keySpace / 2, family), KeyOf(keySpace / 3, family), KeyOf(keySpace - 1, family),
    ];

    static private void AssertUniqueMatches(
        BTreeIndex<string, OrdinalStringComparer> index,
        List<(string? Key, int Offset)> model,
        int family,
        int keySpace,
        string at
    ) {
        Assert.That(index.Count, Is.EqualTo(model.Count), $"{at}: Count");
        Assert.That(Read(index.GetOffsetsIter()), Is.EqualTo(model.Select(e => e.Offset).ToArray()), $"{at}: ordinal scan");

        foreach (var probe in Probes(family, keySpace)) {
            var hit = model.FindIndex(e => string.CompareOrdinal(e.Key, probe) == 0);
            var result = index.GetOffset(probe!);
            if (hit >= 0) {
                Assert.That(result.IsOk(), Is.True, $"{at}: '{probe}' must resolve");
                Assert.That(result.Unwrap(), Is.EqualTo(model[hit].Offset), $"{at}: offset of '{probe}'");
            } else {
                Assert.That(result.IsError(), Is.True, $"{at}: '{probe}' must not resolve");
            }

            Assert.That(Read(index.GetOffsetsGt(probe!)), Is.EqualTo(Where(model, k => string.CompareOrdinal(k, probe) > 0)), $"{at}: Gt('{probe}')");
            Assert.That(Read(index.GetOffsetsGte(probe!)), Is.EqualTo(Where(model, k => string.CompareOrdinal(k, probe) >= 0)), $"{at}: Gte('{probe}')");
            Assert.That(Read(index.GetOffsetsLt(probe!)), Is.EqualTo(Where(model, k => string.CompareOrdinal(k, probe) < 0)), $"{at}: Lt('{probe}')");
            Assert.That(Read(index.GetOffsetsLte(probe!)), Is.EqualTo(Where(model, k => string.CompareOrdinal(k, probe) <= 0)), $"{at}: Lte('{probe}')");
            Assert.That(Read(index.GetOffsetsExcept(probe!)), Is.EqualTo(Where(model, k => string.CompareOrdinal(k, probe) != 0)), $"{at}: Except('{probe}')");
        }
    }

    static private void ApplyUnique(
        BTreeIndex<string, OrdinalStringComparer> index,
        List<(string? Key, int Offset)> model,
        string? key,
        int offset,
        bool insert
    ) {
        var at = model.FindIndex(e => string.CompareOrdinal(e.Key, key) == 0);
        if (insert) {
            if (at >= 0) return;
            index.Insert(key!, offset);
            model.Add((key, offset));
            model.Sort(ByKeyThenOffset);
        } else {
            index.Delete(key!);
            if (at >= 0) model.RemoveAt(at);
        }
    }

    [TestCase(0, 1u)]
    [TestCase(1, 2u)]
    [TestCase(2, 3u)]
    [TestCase(3, 4u)]
    public void Unique_RandomOperations_AgreeWithAnOrdinalModel(int family, uint seed) {
        const int keySpace = 1_200;
        var rng = new XorShift(seed);
        var index = new BTreeIndex<string, OrdinalStringComparer>(ChunkSize);
        var model = new List<(string? Key, int Offset)>();

        for (var op = 1; op <= 4_000; op++) {
            ApplyUnique(index, model, KeyOf(rng.Next(keySpace), family), op, insert: rng.Next(3) < 2);
            if (op % 500 == 0) AssertUniqueMatches(index, model, family, keySpace, $"family {family}, op {op}");
        }
    }

    [Test]
    public void Unique_AKeyThatDivergesEarlierArrivesLate_TheAnchorShrinksAndEveryKeyStillResolves() {
        // 2,000 keys build a long anchor ("player-0000"), then a key that shares only "p"
        // arrives and must force every packed prefix to be recomputed against "p".
        var index = new BTreeIndex<string, OrdinalStringComparer>(ChunkSize);
        var model = new List<(string? Key, int Offset)>();
        for (var i = 0; i < 2_000; i++) ApplyUnique(index, model, KeyOf(i, 0), i, insert: true);
        AssertUniqueMatches(index, model, 0, 2_000, "long anchor");

        ApplyUnique(index, model, "pawn", 9_001, insert: true);
        AssertUniqueMatches(index, model, 0, 2_000, "anchor shrunk to 'p'");

        ApplyUnique(index, model, "zebra", 9_002, insert: true);
        AssertUniqueMatches(index, model, 0, 2_000, "anchor shrunk to nothing");
    }

    [Test]
    public void Unique_DrainedAndRefilledWithADifferentFamily_StartsAFreshAnchor() {
        var index = new BTreeIndex<string, OrdinalStringComparer>(ChunkSize);
        var model = new List<(string? Key, int Offset)>();
        for (var i = 0; i < 500; i++) ApplyUnique(index, model, "player-" + i.ToString("D9"), i, insert: true);
        for (var i = 0; i < 500; i++) ApplyUnique(index, model, "player-" + i.ToString("D9"), 0, insert: false);
        Assert.That(index.Count, Is.EqualTo(0));

        for (var i = 0; i < 500; i++) ApplyUnique(index, model, "club-" + i.ToString("D5"), i, insert: true);

        AssertUniqueMatches(index, model, 0, 500, "refilled with club- keys");
        Assert.That(index.GetOffset("club-00042").Unwrap(), Is.EqualTo(42));
    }

    [TestCase(0)]
    [TestCase(2)]
    [TestCase(3)]
    public void Unique_BulkLoaded_AgreesWithTheModel_AndKeepsAgreeingUnderChurn(int family) {
        const int keySpace = 1_500;
        var rng = new XorShift(17);
        var keys = new List<string?>();
        var offsets = new List<int>();
        var model = new List<(string? Key, int Offset)>();
        for (var i = 0; i < keySpace; i += 2) {
            var key = KeyOf(i, family);
            if (model.Exists(e => string.CompareOrdinal(e.Key, key) == 0)) continue;
            keys.Add(key);
            offsets.Add(i);
            model.Add((key, i));
        }
        model.Sort(ByKeyThenOffset);

        var index = BTreeIndex<string, OrdinalStringComparer>.BulkLoad(keys.ToArray()!, offsets.ToArray(), ChunkSize);
        AssertUniqueMatches(index, model, family, keySpace, "bulk loaded");

        for (var op = 1; op <= 2_000; op++) {
            ApplyUnique(index, model, KeyOf(rng.Next(keySpace), family), 10_000 + op, insert: rng.Next(2) == 0);
            if (op % 500 == 0) AssertUniqueMatches(index, model, family, keySpace, $"after bulk, op {op}");
        }
    }

    [TestCase(0, 7u)]
    [TestCase(2, 8u)]
    [TestCase(3, 9u)]
    public void NonUnique_RandomOperations_AgreeWithAnOrdinalModelOfPairs(int family, uint seed) {
        const int keySpace = 150;
        var rng = new XorShift(seed);
        var index = new NonUniqueBTreeIndex<string, OrdinalStringComparer>(ChunkSize);
        var model = new List<(string? Key, int Offset)>();
        var nextOffset = 0;

        for (var op = 1; op <= 4_000; op++) {
            if (model.Count == 0 || rng.Next(10) < 6) {
                var key = KeyOf(rng.Next(keySpace), family);
                var offset = (int)((nextOffset++ * 7_919L) % 1_000_003);
                index.Insert(key!, offset);
                model.Add((key, offset));
                model.Sort(ByKeyThenOffset);
            } else {
                var victim = rng.Next(model.Count);
                var (key, offset) = model[victim];
                model.RemoveAt(victim);
                index.Delete(key!, offset);
            }

            if (op % 500 != 0) continue;
            var at = $"family {family}, op {op}";
            Assert.That(index.Count, Is.EqualTo(model.Count), $"{at}: Count");
            Assert.That(Read(index.GetOffsetsIter()), Is.EqualTo(model.Select(e => e.Offset).ToArray()), $"{at}: (key, offset) scan");
            foreach (var probe in Probes(family, keySpace)) {
                Assert.That(Read(index.GetOffsets(probe!)), Is.EqualTo(Where(model, k => string.CompareOrdinal(k, probe) == 0)), $"{at}: GetOffsets('{probe}')");
                Assert.That(Read(index.GetOffsetsGt(probe!)), Is.EqualTo(Where(model, k => string.CompareOrdinal(k, probe) > 0)), $"{at}: Gt('{probe}')");
                Assert.That(Read(index.GetOffsetsLte(probe!)), Is.EqualTo(Where(model, k => string.CompareOrdinal(k, probe) <= 0)), $"{at}: Lte('{probe}')");
            }
        }
    }
}
