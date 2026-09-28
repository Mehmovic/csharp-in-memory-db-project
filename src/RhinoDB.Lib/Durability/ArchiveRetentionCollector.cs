using RhinoDB.Lib.Execution;

namespace RhinoDB.Lib.Durability;

internal sealed class ArchiveRetentionCollector<TTx> : IDisposable where TTx : ITransaction {
    private readonly DbContext<TTx> db;
    private readonly Action<ArchiveRetentionRunReport>? onReport;
    private readonly Timer timer;
    private bool disposed;

    internal ArchiveRetentionCollector(
        DbContext<TTx> db,
        ArchiveRetentionPolicy policy,
        Action<ArchiveRetentionRunReport>? onReport = null) {
        this.db = db;
        this.onReport = onReport;
        Policy = policy;
        timer = new Timer(_ => OnTick(), null, policy.Interval, policy.Interval);
    }

    public ArchiveRetentionPolicy Policy { get; }

    private void OnTick() {
        try {
            if (disposed || db.Cold is null) return;
            _ = RunOnce();
        } catch {
            // Swallowed on purpose: a failed collection must never take the server down, and the
            // next tick tries again.
        }
    }

    private async Task<ArchiveRetentionRunReport> RunOnce(DateTimeOffset? now = null) {
        var at = now ?? DateTimeOffset.UtcNow;
        var cutoff = Policy.CutoffUtcTicks(at);

        var result = await db.Run((ctx, _, param) => ctx.Cold!.PruneArchiveOlderThan(param), cutoff);
        var report = new ArchiveRetentionRunReport(at, cutoff, Policy, result.IsOk() ? result.Unwrap() : 0);
        onReport?.Invoke(report);
        return report;
    }

    public void Dispose() {
        if (disposed) return;
        disposed = true;
        try { timer.Dispose(); } catch { /* best-effort */ }
    }
}

public readonly record struct ArchiveRetentionRunReport(
    DateTimeOffset RanAt,
    long CutoffUtcTicks,
    ArchiveRetentionPolicy Policy,
    int DeletedSegments
);