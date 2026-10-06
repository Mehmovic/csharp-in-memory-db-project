using RhinoDB.Core;
using RhinoDB.Lib.Realtime;

namespace RhinoDB.Lib.Execution.Test;

// The RhinoRandom behind ctx.Random: no seed until the first draw, then one unpredictable seed for the request (or
// transaction), kept inside the engine.
public class LazyRhinoRandomTests {
    [Test]
    public void NoSeedExists_UntilTheFirstAccess() {
        var random = default(LazyRhinoRandom);

        Assert.That(random.Seed, Is.Null, "a request that never rolls pays nothing.");

        _ = random.Value.NextUInt64();

        Assert.That(random.Seed, Is.Not.Null);
    }

    [Test]
    public void TheSequence_IsRhinoRandomOfTheRecordedSeed_SoAReplayReproducesIt() {
        var random = default(LazyRhinoRandom);
        var draws = Enumerable.Range(0, 5).Select(_ => random.Value.NextUInt64().Unwrap()).ToArray();

        var replay = new RhinoRandom(random.Seed!.Value);

        Assert.That(Enumerable.Range(0, 5).Select(_ => replay.NextUInt64().Unwrap()), Is.EqualTo(draws));
    }

    [Test]
    public void TheSeed_IsTakenOnce_AndDrawsContinueTheSameSequence() {
        var random = default(LazyRhinoRandom);
        var first = random.Value.NextUInt64().Unwrap();
        var seed = random.Seed;
        var second = random.Value.NextUInt64().Unwrap();

        Assert.That(random.Seed, Is.EqualTo(seed));
        Assert.That(second, Is.Not.EqualTo(first));
    }

    [Test]
    public void TwoInstances_GetDifferentSeeds() {
        var seeds = Enumerable.Range(0, 50).Select(i => {
            var random = default(LazyRhinoRandom);
            _ = random.Value;
            return random.Seed!.Value;
        }).ToHashSet();

        Assert.That(seeds, Has.Count.EqualTo(50));
    }

    [Test]
    public void RhinoCtxRandom_DrawsFromOneSequencePerContext() {
        var ctx = new RhinoCtx(new DbContext(), Identity.System);
        Assert.That(ctx.RandomSeed, Is.Null);

        var draws = Enumerable.Range(0, 3).Select(_ => ctx.Random.NextUInt64().Unwrap()).ToArray();

        var replay = new RhinoRandom(ctx.RandomSeed!.Value);
        Assert.That(Enumerable.Range(0, 3).Select(_ => replay.NextUInt64().Unwrap()), Is.EqualTo(draws),
            "ctx.Random is a ref to one generator - not a fresh copy per access.");
    }
}
