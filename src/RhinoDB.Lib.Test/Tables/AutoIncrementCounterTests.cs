namespace RhinoDB.Lib.Tables.Test;

// AutoIncrementCounter hands out ids for [AutoIncrement] primary keys. It is only exercised
// indirectly today (through generated code in LoaderTests), and its one interesting property is
// easy to break silently: Seed must never rewind.
//
// The counter advances at staging time, not at apply time, so a reverted operation consumes
// ids and leaves a gap - deliberately, since reusing one would hand a client an id that already
// existed. That makes reload the other half of the contract: the loader seeds the counter from
// the highest key present in the restored data, and if Seed could move the counter backwards it
// would re-issue an id that a client has already seen.
public class AutoIncrementCounterTests {
    [Test]
    public void Next_StartsAtOneByDefault() {
        var counter = new AutoIncrementCounter();

        Assert.That(counter.Next(), Is.EqualTo(1));
    }

    [Test]
    public void Next_HandsOutStrictlyIncreasingValues() {
        var counter = new AutoIncrementCounter();

        var values = new[] { counter.Next(), counter.Next(), counter.Next(), counter.Next() };

        Assert.That(values, Is.EqualTo(new long[] { 1, 2, 3, 4 }));
    }

    [Test]
    public void AnExplicitStart_OverridesTheDefault() {
        var counter = new AutoIncrementCounter(1000);

        Assert.Multiple(() => {
            Assert.That(counter.Next(), Is.EqualTo(1000));
            Assert.That(counter.Next(), Is.EqualTo(1001));
        });
    }

    [Test]
    public void Seed_WithAHigherValue_JumpsForward() {
        var counter = new AutoIncrementCounter();
        counter.Next();
        counter.Next();

        counter.Seed(500);

        Assert.That(counter.Next(), Is.EqualTo(500), "Seed must move the counter to at least minNext.");
    }

    [Test]
    public void Seed_WithAnEqualValue_LeavesTheCounterAlone() {
        var counter = new AutoIncrementCounter();
        counter.Next();
        counter.Next();
        counter.Next(); // next == 4

        counter.Seed(4);

        Assert.That(counter.Next(), Is.EqualTo(4), "seeding to the current value must not consume it.");
    }

    [Test]
    public void Seed_WithALowerValue_NeverRewinds() {
        var counter = new AutoIncrementCounter(100);
        counter.Next();
        counter.Next();
        counter.Next(); // next == 103

        counter.Seed(5);
        counter.Seed(0);
        counter.Seed(-1);

        Assert.That(counter.Next(), Is.EqualTo(103),
            "a rewind would re-issue an id a client may already hold - the counter only moves forward.");
    }

    [Test]
    public void Seed_RepeatedWithTheSameHigherValue_StaysAtThatValue() {
        // The loader seeds per row as it restores, so Seed is called many times with the same
        // running maximum. It must be idempotent, not cumulative.
        var counter = new AutoIncrementCounter();
        counter.Next();

        counter.Seed(42);
        counter.Seed(42);
        counter.Seed(42);

        Assert.That(counter.Next(), Is.EqualTo(42));
    }

    [Test]
    public void Seed_AfterNext_ContinuesFromTheSeededFloorNotFromTheOldValue() {
        var counter = new AutoIncrementCounter();
        counter.Next(); // 1
        counter.Seed(10);
        counter.Next(); // 10
        counter.Seed(20);

        Assert.Multiple(() => {
            Assert.That(counter.Next(), Is.EqualTo(20));
            Assert.That(counter.Next(), Is.EqualTo(21));
        });
    }

    [Test]
    public void Next_DoesNotRepeatAcrossManyCalls() {
        var counter = new AutoIncrementCounter();

        var seen = new HashSet<long>();
        for (var i = 0; i < 10_000; i++) Assert.That(seen.Add(counter.Next()), Is.True);

        Assert.That(seen.Count, Is.EqualTo(10_000));
    }
}