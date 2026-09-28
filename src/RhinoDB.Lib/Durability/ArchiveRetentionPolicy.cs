using RhinoDB.SchemaContracts;

namespace RhinoDB.Lib.Durability;

public sealed record ArchiveRetentionPolicy(TimeSpan Interval, TimeSpan KeepFor) {
    static public ArchiveRetentionPolicy? FromConfig(ArchiveRetentionConfig? config) {
        if (config is not { Enabled: true }) return null;

        var interval = config.ResolvedInterval;
        var keepFor = config.ResolvedKeepFor;

        if (interval is null || keepFor is null)
            throw new GeneratorConfigException(
                "Server.ArchiveRetention is enabled but does not set both Interval and KeepFor. " +
                "Interval is HOW OFTEN the collector runs; KeepFor is HOW MUCH SURVIVES.");

        if (interval.Value <= TimeSpan.Zero)
            throw new GeneratorConfigException($"Server.ArchiveRetention.Interval must be greater than zero, got {interval.Value}.");
        if (keepFor.Value <= TimeSpan.Zero)
            throw new GeneratorConfigException($"Server.ArchiveRetention.KeepFor must be greater than zero, got {keepFor.Value}.");

        return new ArchiveRetentionPolicy(interval.Value, keepFor.Value);
    }

    public long CutoffUtcTicks(DateTimeOffset now) => now.UtcTicks - KeepFor.Ticks;
}