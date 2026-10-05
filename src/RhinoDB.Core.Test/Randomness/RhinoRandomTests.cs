namespace RhinoDB.Core.Randomness.Test;

// RhinoRandom's sequence is a contract: replay and prediction re-run a transaction with its recorded seed and must get
// the same draws on every OS and .NET version. The pinned values below come from an independent reference
// implementation of SplitMix64 + xoshiro256** (Python, from the published algorithms; SplitMix64(0)'s first output is
// the well-known 0xE220A8397B1DCDAF) - if one of these tests fails, the algorithm changed and every recorded seed broke.
// Every API returns a Result and never throws; invalid input comes back as an error kind.
public class RhinoRandomTests {
    // ---- the pinned sequence ----

    [TestCase(0UL, 0x99EC5F36CB75F2B4UL, 0xBF6E1F784956452AUL, 0x1A5F849D4933E6E0UL, 0x6AA594F1262D2D2CUL, 0xBBA5AD4A1F842E59UL)]
    [TestCase(42UL, 0x15780B2E0C2EC716UL, 0x6104D9866D113A7EUL, 0xAE17533239E499A1UL, 0xECB8AD4703B360A1UL, 0xFDE6DC7FE2EC5E64UL)]
    [TestCase(ulong.MaxValue, 0x8F5520D52A7EAD08UL, 0xC476A018CAA1802DUL, 0x81DE31C0D260469EUL, 0xBF658D7E065F3C2FUL, 0x913593FDA1BCA32AUL)]
    public void NextUInt64_ForAKnownSeed_MatchesTheReferenceImplementation(ulong seed, ulong a, ulong b, ulong c, ulong d, ulong e) {
        var random = new RhinoRandom(seed);

        var drawn = Enumerable.Range(0, 5).Select(_ => random.NextUInt64().Unwrap()).ToArray();

        Assert.That(drawn, Is.EqualTo(new[] { a, b, c, d, e }));
    }

    [Test]
    public void Next_ForAKnownSeed_MatchesTheReferenceBoundedDraws() {
        var random = new RhinoRandom(42);

        var drawn = Enumerable.Range(0, 10).Select(_ => random.Next(100).Unwrap()).ToArray();

        Assert.That(drawn, Is.EqualTo(new[] { 8, 37, 68, 92, 99, 76, 71, 85, 76, 58 }), "pins the bounded-draw method too, not just the raw generator.");
    }

    [Test]
    public void NextDouble_ForAKnownSeed_MatchesTheReference() {
        var random = new RhinoRandom(7);

        var drawn = Enumerable.Range(0, 3).Select(_ => random.NextDouble().Unwrap()).ToArray();

        Assert.That(drawn, Is.EqualTo(new[] { 0.7005764821796896, 0.2787512294737843, 0.8396274618764198 }));
    }

    [Test]
    public void TheSameSeed_GivesTheSameSequence_AndADifferentSeed_ADifferentOne() {
        var first = new RhinoRandom(1234);
        var second = new RhinoRandom(1234);
        var other = new RhinoRandom(1235);

        var a = Enumerable.Range(0, 100).Select(_ => first.NextUInt64().Unwrap()).ToArray();
        var b = Enumerable.Range(0, 100).Select(_ => second.NextUInt64().Unwrap()).ToArray();
        var c = Enumerable.Range(0, 100).Select(_ => other.NextUInt64().Unwrap()).ToArray();

        Assert.That(b, Is.EqualTo(a));
        Assert.That(c, Is.Not.EqualTo(a));
    }

    [Test]
    public void ACopy_ForksTheSequence_SoTheStructMustBeHeldByRef() {
        var original = new RhinoRandom(99);
        var copy = original;

        var fromCopy = copy.NextUInt64().Unwrap();
        var fromOriginal = original.NextUInt64().Unwrap();

        Assert.That(fromOriginal, Is.EqualTo(fromCopy), "a copy replays the same draws - pass it by ref, never by value.");
    }

    // ---- bounds ----

    [Test]
    public void Next_StaysWithinBounds_AndCoversEveryValue() {
        var random = new RhinoRandom(1);
        var seen = new bool[7];

        for (var i = 0; i < 10_000; i++) {
            var value = random.Next(7).Unwrap();
            Assert.That(value, Is.InRange(0, 6));
            seen[value] = true;
        }

        Assert.That(seen, Is.All.True);
    }

    [Test]
    public void Next_OfOne_IsAlwaysZero() {
        var random = new RhinoRandom(5);

        Assert.That(Enumerable.Range(0, 100).Select(_ => random.Next(1).Unwrap()), Is.All.EqualTo(0));
    }

    [Test]
    public void Range_HandlesNegativeAndFullWidthSpans() {
        var random = new RhinoRandom(3);

        for (var i = 0; i < 10_000; i++) {
            Assert.That(random.Range(-5, 5).Unwrap(), Is.InRange(-5, 4));
            Assert.That(random.Range(int.MinValue, int.MaxValue).Unwrap(), Is.InRange(int.MinValue, int.MaxValue - 1));
            Assert.That(random.Range(long.MinValue, long.MaxValue).Unwrap(), Is.InRange(long.MinValue, long.MaxValue - 1));
        }
    }

    [Test]
    public void NextDouble_AndNextFloat_StayInTheHalfOpenUnitInterval() {
        var random = new RhinoRandom(11);

        for (var i = 0; i < 100_000; i++) {
            var d = random.NextDouble().Unwrap();
            var f = random.NextFloat().Unwrap();
            Assert.That(d >= 0 && d < 1, Is.True, $"double {d}");
            Assert.That(f >= 0 && f < 1, Is.True, $"float {f}");
        }
    }

    // ---- invalid input: an error kind, never an exception ----

    [Test]
    public void InvalidInput_ComesBackAsAnErrorKind_AndNothingThrows() {
        var random = new RhinoRandom(5);

        Assert.Multiple(() => {
            Assert.That(random.Next(0).GetError().Kind, Is.EqualTo(ErrorKind.RandomBoundInvalid));
            Assert.That(random.Next(-3).GetError().Kind, Is.EqualTo(ErrorKind.RandomBoundInvalid));
            Assert.That(random.Next(0L).GetError().Kind, Is.EqualTo(ErrorKind.RandomBoundInvalid));
            Assert.That(random.Range(5, 5).GetError().Kind, Is.EqualTo(ErrorKind.RandomBoundInvalid));
            Assert.That(random.Range(5L, 4L).GetError().Kind, Is.EqualTo(ErrorKind.RandomBoundInvalid));
            Assert.That(random.Chance(double.NaN).GetError().Kind, Is.EqualTo(ErrorKind.RandomProbabilityInvalid));
            Assert.That(random.Pick(ReadOnlySpan<string>.Empty).GetError().Kind, Is.EqualTo(ErrorKind.RandomNothingToPick));
            Assert.That(random.WeightedPick(new[] { 1, -1 }).GetError().Kind, Is.EqualTo(ErrorKind.RandomWeightsInvalid));
            Assert.That(random.WeightedPick(new[] { 0, 0 }).GetError().Kind, Is.EqualTo(ErrorKind.RandomWeightsInvalid));
            Assert.That(random.WeightedPick(Array.Empty<int>()).GetError().Kind, Is.EqualTo(ErrorKind.RandomWeightsInvalid));
            Assert.That(random.WeightedPick(new[] { 1.0, double.NaN }).GetError().Kind, Is.EqualTo(ErrorKind.RandomWeightsInvalid));
            Assert.That(random.WeightedPick(new[] { double.PositiveInfinity }).GetError().Kind, Is.EqualTo(ErrorKind.RandomWeightsInvalid));
            Assert.That(random.WeightedPick(new[] { 0.0 }).GetError().Kind, Is.EqualTo(ErrorKind.RandomWeightsInvalid));
        });
    }

    [Test]
    public void AnInvalidCall_DoesNotConsumeADraw() {
        var probe = new RhinoRandom(21);
        var expected = probe.NextUInt64().Unwrap();
        var random = new RhinoRandom(21);

        random.Next(0);
        random.Range(9, 3);
        random.Pick(ReadOnlySpan<int>.Empty);
        random.WeightedPick(new[] { -1 });

        Assert.That(random.NextUInt64().Unwrap(), Is.EqualTo(expected), "rejected input never advances the sequence - replays stay aligned.");
    }

    // ---- distribution (deterministic seeds, so these can't flake) ----

    [Test]
    public void Next_IsUniform_WithNoModuloBias() {
        // 3 doesn't divide 2^32 - a modulo-based draw would favour the low values slightly; at 600k draws a
        // chi-square over 3 buckets stays far below the 0.1% critical value (13.8) for an unbiased generator.
        var random = new RhinoRandom(2024);
        var counts = new long[3];
        const int draws = 600_000;

        for (var i = 0; i < draws; i++) counts[random.Next(3).Unwrap()]++;

        var expected = draws / 3.0;
        var chiSquare = counts.Sum(count => (count - expected) * (count - expected) / expected);
        Assert.That(chiSquare, Is.LessThan(13.8), $"counts: {string.Join(", ", counts)}");
    }

    [Test]
    public void Chance_RespectsItsEdges_AndItsProbability() {
        var random = new RhinoRandom(8);

        Assert.That(Enumerable.Range(0, 1000).Any(_ => random.Chance(0).Unwrap()), Is.False);
        Assert.That(Enumerable.Range(0, 1000).All(_ => random.Chance(1).Unwrap()), Is.True);
        Assert.That(Enumerable.Range(0, 1000).Any(_ => random.Chance(-0.5).Unwrap()), Is.False);

        var hits = Enumerable.Range(0, 100_000).Count(_ => random.Chance(0.25).Unwrap());
        Assert.That(hits, Is.InRange(24_000, 26_000));
    }

    // ---- collections ----

    [Test]
    public void Pick_ReturnsAnElement() {
        var random = new RhinoRandom(13);
        string[] items = ["a", "b", "c"];

        for (var i = 0; i < 100; i++) Assert.That(items, Does.Contain(random.Pick<string>(items).Unwrap()));
    }

    [Test]
    public void Shuffle_IsAPermutation_AndDeterministicForASeed() {
        int[] first = Enumerable.Range(0, 52).ToArray();
        int[] second = Enumerable.Range(0, 52).ToArray();
        var a = new RhinoRandom(52);
        var b = new RhinoRandom(52);

        Assert.That(a.Shuffle<int>(first).IsOk(), Is.True);
        b.Shuffle<int>(second);

        Assert.That(first.Order(), Is.EqualTo(Enumerable.Range(0, 52)), "every card is still there exactly once.");
        Assert.That(first, Is.Not.EqualTo(Enumerable.Range(0, 52)), "and they moved.");
        Assert.That(second, Is.EqualTo(first), "the same seed shuffles the same way.");
    }

    [Test]
    public void Shuffle_OfZeroOrOneElements_IsANoOp() {
        var random = new RhinoRandom(1);
        int[] one = [7];

        Assert.That(random.Shuffle<int>([]).IsOk(), Is.True);
        Assert.That(random.Shuffle<int>(one).IsOk(), Is.True);

        Assert.That(one, Is.EqualTo(new[] { 7 }));
    }

    [Test]
    public void WeightedPick_WithIntegerWeights_FollowsTheWeights_AndNeverPicksAZeroWeight() {
        var random = new RhinoRandom(77);
        int[] weights = [70, 0, 25, 5];
        var counts = new int[4];

        for (var i = 0; i < 100_000; i++) counts[random.WeightedPick(weights).Unwrap()]++;

        Assert.That(counts[1], Is.EqualTo(0));
        Assert.That(counts[0], Is.InRange(69_000, 71_000));
        Assert.That(counts[2], Is.InRange(24_000, 26_000));
        Assert.That(counts[3], Is.InRange(4_500, 5_500));
    }

    [Test]
    public void WeightedPick_WithDoubleWeights_FollowsTheWeights_AndNeverPicksAZeroWeight() {
        var random = new RhinoRandom(78);
        double[] weights = [0.5, 0.0, 0.5];
        var counts = new int[3];

        for (var i = 0; i < 100_000; i++) counts[random.WeightedPick(weights).Unwrap()]++;

        Assert.That(counts[1], Is.EqualTo(0));
        Assert.That(counts[0], Is.InRange(49_000, 51_000));
    }
}
