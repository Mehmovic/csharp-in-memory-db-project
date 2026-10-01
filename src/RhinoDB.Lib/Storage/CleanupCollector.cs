namespace RhinoDB.Lib.Storage;

public enum CleanupTrigger {
    PerOperation = 0,
    PerTime = 1,
}

public sealed class CleanupCollector(CleanupTrigger trigger, TimeSpan interval) {
    private CleanupTrigger Trigger { get; } = trigger;

    private readonly long intervalMs = (long)interval.TotalMilliseconds;

    private long lastSweep;

    public CleanupCollector(CleanupTrigger trigger) : this(trigger, TimeSpan.FromSeconds(5)) { }

    public bool ShouldSweep(int pending, Func<long>? clock = null) {
        if (pending == 0) return false;
        if (Trigger == CleanupTrigger.PerOperation) return true;

        var now = clock?.Invoke() ?? Environment.TickCount64;
        if (now - lastSweep < intervalMs) return false;
        lastSweep = now;
        return true;
    }
}