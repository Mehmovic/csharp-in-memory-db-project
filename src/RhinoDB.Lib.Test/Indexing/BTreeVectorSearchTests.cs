using System.Numerics;

using RhinoDB.Core;

namespace RhinoDB.Lib.Indexing.Test;

// int/long/uint/ulong keys on DefaultComparer<T> finish every binary search with a Vector256
// "count the lanes below the key" over the last 16 entries. The vector compare is signed or
// unsigned depending on T, so a wrong lane type silently misorders exactly the values where the
// two disagree: negatives for signed keys, and values with the top bit set for unsigned keys.
// Each test therefore mixes those extremes into a random workload and checks every read surface
// against a SortedDictionary.
public class BTreeVectorSearchTests {
    private struct XorShift(ulong seed) {
        private ulong state = seed | 1ul;

        public ulong Next() {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            return state;
        }
    }

    static private int[] Read(StackArrayPoolContainer<int> container) {
        using var c = container;
        return c.Buffer().ToArray();
    }

    static private void RunAgainstModel<T>(Func<ulong, T> keyOf, T[] extremes, int chunkSize)
        where T : struct, IComparable<T>, IEquatable<T>, IComparisonOperators<T, T, bool> {
        var rng = new XorShift(0x9E3779B97F4A7C15);
        var index = new BTreeIndex<T, DefaultComparer<T>>(chunkSize);
        var model = new SortedDictionary<T, int>();
        var probes = new List<T>(extremes);

        void Insert(T key, int offset) {
            if (model.ContainsKey(key)) return;
            index.Insert(key, offset);
            model[key] = offset;
        }

        foreach (var key in extremes) Insert(key, model.Count);

        for (var op = 1; op <= 6_000; op++) {
            var key = keyOf(rng.Next());
            if (op % 5 == 0) probes.Add(key);
            if (rng.Next() % 3 == 0) {
                index.Delete(key);
                model.Remove(key);
            } else {
                Insert(key, op);
            }

            if (op % 1_500 != 0) continue;
            Assert.That(index.Count, Is.EqualTo(model.Count), $"{typeof(T).Name} op {op}: Count");
            Assert.That(Read(index.GetOffsetsIter()), Is.EqualTo(model.Values.ToArray()), $"{typeof(T).Name} op {op}: scan order");
            foreach (var probe in probes.TakeLast(40).Concat(extremes)) {
                var found = index.GetOffset(probe);
                Assert.That(found.IsOk(), Is.EqualTo(model.ContainsKey(probe)), $"{typeof(T).Name}: presence of {probe}");
                if (found.IsOk()) Assert.That(found.Unwrap(), Is.EqualTo(model[probe]), $"{typeof(T).Name}: offset of {probe}");
                Assert.That(Read(index.GetOffsetsGt(probe)), Is.EqualTo(model.Where(e => e.Key > probe).Select(e => e.Value).ToArray()), $"{typeof(T).Name}: Gt({probe})");
                Assert.That(Read(index.GetOffsetsLte(probe)), Is.EqualTo(model.Where(e => e.Key <= probe).Select(e => e.Value).ToArray()), $"{typeof(T).Name}: Lte({probe})");
            }
        }
    }

    [TestCase(16)]
    [TestCase(256)]
    public void IntKeys_IncludingNegatives_AgreeWithTheModel(int chunkSize)
        => RunAgainstModel(r => (int)r % 5_000, [int.MinValue, int.MinValue + 1, -1, 0, 1, int.MaxValue - 1, int.MaxValue], chunkSize);

    [TestCase(16)]
    [TestCase(256)]
    public void LongKeys_IncludingNegatives_AgreeWithTheModel(int chunkSize)
        => RunAgainstModel(r => (long)r % 50_000L * 1_000_000_007L, [long.MinValue, long.MinValue + 1, -1L, 0L, 1L, long.MaxValue - 1, long.MaxValue], chunkSize);

    [TestCase(16)]
    [TestCase(256)]
    public void UIntKeys_AboveIntMaxValue_AgreeWithTheModel(int chunkSize)
        => RunAgainstModel(r => (uint)(r % 5_000) * 900_001u, [0u, 1u, (uint)int.MaxValue, (uint)int.MaxValue + 1, uint.MaxValue - 1, uint.MaxValue], chunkSize);

    [TestCase(16)]
    [TestCase(256)]
    public void ULongKeys_AboveLongMaxValue_AgreeWithTheModel(int chunkSize)
        => RunAgainstModel(r => r % 50_000 * 368_934_881_474_191ul, [0ul, 1ul, (ulong)long.MaxValue, (ulong)long.MaxValue + 1, ulong.MaxValue - 1, ulong.MaxValue], chunkSize);
}
