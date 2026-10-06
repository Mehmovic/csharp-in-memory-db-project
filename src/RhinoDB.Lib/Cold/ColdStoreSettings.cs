using RhinoDB.SchemaContracts;

namespace RhinoDB.Lib.Cold;

public sealed record ColdStoreSettings(long WalFlushThresholdBytes, TimeSpan WalFlushInterval, long EvictionBatchThresholdBytes, TimeSpan PrefsCacheDuration) {
    static public readonly ColdStoreSettings Default = FromConfig(new DurabilityConfig());

    static public ColdStoreSettings FromConfig(DurabilityConfig config, PrefsConfig? prefs = null) {
        if (config.WalFlushThresholdBytes <= 0)
            throw new GeneratorConfigException($"Durability.WalFlushThresholdBytes must be greater than zero, got {config.WalFlushThresholdBytes}.");
        if (config.EvictionBatchThresholdBytes <= 0)
            throw new GeneratorConfigException($"Durability.EvictionBatchThresholdBytes must be greater than zero, got {config.EvictionBatchThresholdBytes}.");
        
        return new ColdStoreSettings(
            config.WalFlushThresholdBytes,
            config.ResolvedWalFlushInterval,
            config.EvictionBatchThresholdBytes,
            (prefs ?? new PrefsConfig()).ResolvedCacheDuration
        );
    }
}
