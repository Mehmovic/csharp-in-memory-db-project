using RhinoDB.Lib.Storage;

namespace RhinoDB.Lib.Storage.Test;

public class CleanupCollectorTests {
    static private long FakeClock(long value) => value;

    [Test]
    public void PerOperation_SweepsWheneverSomethingIsPending() {
        var c = new CleanupCollector(CleanupTrigger.PerOperation);
        Assert.That(c.ShouldSweep(1, () => 0), Is.True);
        Assert.That(c.ShouldSweep(500, () => 0), Is.True, "a burst is still one sweep, which is the batching win");
    }

    [Test]
    public void PerOperation_NeverSweepsWithNothingPending() {
        var c = new CleanupCollector(CleanupTrigger.PerOperation);
        Assert.That(c.ShouldSweep(0, () => 0), Is.False);
    }

    [Test]
    public void PerTime_SkipsTheFirstSweepUntilTheIntervalHasElapsed() {
        var c = new CleanupCollector(CleanupTrigger.PerTime, TimeSpan.FromMilliseconds(1000));
        var now = 5_000L;
        Assert.That(c.ShouldSweep(10, () => now), Is.True, "the first pending sweep is never delayed");
    }

    [Test]
    public void PerTime_SkipsWhenTheIntervalHasNotElapsed() {
        var c = new CleanupCollector(CleanupTrigger.PerTime, TimeSpan.FromMilliseconds(1000));
        var now = 5_000L;
        c.ShouldSweep(10, () => now);
        now = 5_500L;
        Assert.That(c.ShouldSweep(10, () => now), Is.False);
        now = 6_000L;
        Assert.That(c.ShouldSweep(10, () => now), Is.True);
    }

    [Test]
    public void PerTime_NeverSweepsWithNothingPending() {
        var c = new CleanupCollector(CleanupTrigger.PerTime, TimeSpan.FromMilliseconds(1000));
        Assert.That(c.ShouldSweep(0, () => 99_000L), Is.False);
    }

    [Test]
    public void PerOperation_DoesNotReadTheClockAtAll() {
        var c = new CleanupCollector(CleanupTrigger.PerOperation);
        var reads = 0;
        c.ShouldSweep(5, () => { reads++; return 0L; });
        c.ShouldSweep(0, () => { reads++; return 0L; });
        c.ShouldSweep(9, () => { reads++; return 0L; });
        Assert.That(reads, Is.EqualTo(0), "PerOperation must not pay for a timestamp it never uses");
    }

    [Test]
    public void PerTime_OnlyReadsTheClockWhenSomethingIsPending() {
        var c = new CleanupCollector(CleanupTrigger.PerTime, TimeSpan.FromMilliseconds(1000));
        var reads = 0;
        c.ShouldSweep(0, () => { reads++; return 0L; });
        Assert.That(reads, Is.EqualTo(0));
        c.ShouldSweep(3, () => { reads++; return 1_000L; });
        Assert.That(reads, Is.EqualTo(1));
    }
}