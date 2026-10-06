using System.Text.Json.Serialization;

namespace RhinoDB.SchemaContracts;

public sealed class DurabilityConfig {
    public const long DefaultWalFlushThresholdBytes = 4 * 1024 * 1024;
    public const long DefaultEvictionBatchThresholdBytes = 4 * 1024 * 1024;

    // Optimistic writes reach disk at whichever comes first: this many buffered bytes, or the interval below.
    public long WalFlushThresholdBytes { get; set; } = DefaultWalFlushThresholdBytes;
    public string WalFlushInterval { get; set; } = "00:00:00.100";
    public long EvictionBatchThresholdBytes { get; set; } = DefaultEvictionBatchThresholdBytes;

    [JsonIgnore]
    public TimeSpan ResolvedWalFlushInterval => ConfigDuration.ParseRequired(WalFlushInterval, "Durability.WalFlushInterval");
}
