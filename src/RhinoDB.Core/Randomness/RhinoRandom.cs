using System.Numerics;

namespace RhinoDB.Core;

public struct RhinoRandom {
    private ulong s0;
    private ulong s1;
    private ulong s2;
    private ulong s3;

    public RhinoRandom(ulong seed) {
        var state = seed;
        s0 = SplitMix64(ref state);
        s1 = SplitMix64(ref state);
        s2 = SplitMix64(ref state);
        s3 = SplitMix64(ref state);
    }

    public Result<ulong> NextUInt64() => NextRaw();

    public Result<uint> NextUInt32() => (uint)(NextRaw() >> 32);

    public Result<int> Next(int maxExclusive) =>
        maxExclusive <= 0 ? Result<int>.Error(DbError.RandomBoundInvalid()) : (int)NextBelow((uint)maxExclusive);

    public Result<long> Next(long maxExclusive) =>
        maxExclusive <= 0 ? Result<long>.Error(DbError.RandomBoundInvalid()) : (long)NextBelow((ulong)maxExclusive);

    public Result<int> Range(int minInclusive, int maxExclusive) =>
        maxExclusive <= minInclusive
            ? Result<int>.Error(DbError.RandomBoundInvalid())
            : (int)(minInclusive + (long)NextBelow((uint)((long)maxExclusive - minInclusive)));

    public Result<long> Range(long minInclusive, long maxExclusive) {
        if (maxExclusive <= minInclusive) return Result<long>.Error(DbError.RandomBoundInvalid());
        unchecked {
            return minInclusive + (long)NextBelow((ulong)maxExclusive - (ulong)minInclusive);
        }
    }

    // [0, 1) with the full 53 / 24 bits of mantissa precision.
    public Result<double> NextDouble() => NextRawDouble();

    public Result<float> NextFloat() => (NextRaw() >> 40) * (1.0f / (1 << 24));

    public Result<bool> NextBool() => (NextRaw() >> 63) != 0;

    public Result<bool> Chance(double probability) {
        if (double.IsNaN(probability)) return Result<bool>.Error(DbError.RandomProbabilityInvalid());
        if (probability <= 0) return false;
        if (probability >= 1) return true;
        return NextRawDouble() < probability;
    }

    public Result<T> Pick<T>(ReadOnlySpan<T> items) =>
        items.IsEmpty ? Result<T>.Error(DbError.RandomNothingToPick()) : items[(int)NextBelow((uint)items.Length)];

    // Fisher-Yates: every permutation equally likely.
    public Result Shuffle<T>(Span<T> items) {
        for (var i = items.Length - 1; i > 0; i--) {
            var j = (int)NextBelow((uint)(i + 1));
            (items[i], items[j]) = (items[j], items[i]);
        }
        return Result.Ok();
    }

    // Index of the picked weight; integer weights are exact (drop tables), a zero weight is never picked.
    public Result<int> WeightedPick(ReadOnlySpan<int> weights) {
        long total = 0;
        foreach (var weight in weights) {
            if (weight < 0) return Result<int>.Error(DbError.RandomWeightsInvalid());
            total += weight;
        }
        if (total == 0) return Result<int>.Error(DbError.RandomWeightsInvalid());

        var roll = (long)NextBelow((ulong)total);
        for (var i = 0; i < weights.Length; i++) {
            roll -= weights[i];
            if (roll < 0) return i;
        }
        return Result<int>.Error(DbError.RandomWeightsInvalid()); // unreachable: the roll is below the total
    }

    public Result<int> WeightedPick(ReadOnlySpan<double> weights) {
        double total = 0;
        foreach (var weight in weights) {
            if (!(weight >= 0) || double.IsInfinity(weight)) return Result<int>.Error(DbError.RandomWeightsInvalid());
            total += weight;
        }
        if (!(total > 0) || double.IsInfinity(total)) return Result<int>.Error(DbError.RandomWeightsInvalid());

        var roll = NextRawDouble() * total;
        var last = -1;
        for (var i = 0; i < weights.Length; i++) {
            if (weights[i] == 0) continue;
            last = i;
            roll -= weights[i];
            if (roll < 0) return i;
        }
        return last; // floating-point rounding left the roll a hair above the last positive weight
    }

    private ulong NextRaw() {
        unchecked {
            var result = BitOperations.RotateLeft(s1 * 5, 7) * 9;
            var t = s1 << 17;
            s2 ^= s0;
            s3 ^= s1;
            s1 ^= s2;
            s0 ^= s3;
            s2 ^= t;
            s3 = BitOperations.RotateLeft(s3, 45);
            return result;
        }
    }

    private double NextRawDouble() => (NextRaw() >> 11) * (1.0 / (1UL << 53));

    // Lemire: (0 - bound) % bound is 2^32 mod bound - the wrap is the point. (uint)product keeps the low 32 bits on purpose.
    private uint NextBelow(uint bound) {
        unchecked {
            var product = (NextRaw() >> 32) * bound;
            var low = (uint)product;
            if (low < bound) {
                var threshold = (0u - bound) % bound;
                while (low < threshold) {
                    product = (NextRaw() >> 32) * bound;
                    low = (uint)product;
                }
            }
            return (uint)(product >> 32);
        }
    }

    // Same, 64-bit: (0UL - bound) % bound is 2^64 mod bound.
    private ulong NextBelow(ulong bound) {
        unchecked {
            var high = Math.BigMul(NextRaw(), bound, out var low);
            if (low < bound) {
                var threshold = (0UL - bound) % bound;
                while (low < threshold) high = Math.BigMul(NextRaw(), bound, out low);
            }
            return high;
        }
    }

    static private ulong SplitMix64(ref ulong state) {
        unchecked {
            state += 0x9E3779B97F4A7C15UL;
            var z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }
}
