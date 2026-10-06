using System.Globalization;
using System.Reflection;

using RhinoDB.Lib.Cold;
using RhinoDB.SchemaContracts;

namespace RhinoDB.Lib.Hosting;

public enum RhinoRunMode {
    Run,
    Replay,
    Migrate,
    WalPrune,
    WalMigrate,
}

public sealed class RhinoHostOptions {
    public required string ColdPath { get; init; }
    public RhinoRunMode Mode { get; private init; } = RhinoRunMode.Run;
    public ulong? ReplayUpToLsn { get; private init; }
    public int? WalKeepGenerations { get; private init; }
    public ulong? WalPruneOlderThanUtcTicks { get; private init; }
    public int HttpPort { get; private init; } = 7777;
    public bool HttpEnabled { get; private init; }
    public uint ServerVersion { get; private init; }
    public NetworkTransportKind Transport { get; private init; } = NetworkTransportKind.WebSocket;
    public ColdStoreSettings ColdStore { get; private init; } = ColdStoreSettings.Default;
    public TimeSpan UnrecoverableShutdownBudget { get; private init; } = TimeSpan.FromSeconds(5);
    public TimeSpan UnrecoverableExitWatchdog { get; private init; } = TimeSpan.FromSeconds(7);

    static public Result<RhinoHostOptions> Load(string configDirectory) {
        RhinoDbConfig config;
        try {
            config = GeneratorConfigLoader.LoadFull(configDirectory);
        } catch (GeneratorConfigException ex) {
            return Result<RhinoHostOptions>.Error(DbError.SystemFailure(ex));
        }

        return FromConfig(config.Host, config.Server.PackedVersion, config.Durability, config.Network);
    }

    static public Result<RhinoHostOptions> FromConfig(
        HostConfig host, uint serverVersion = 0, DurabilityConfig? durability = null, NetworkConfig? network = null) {
        if (!TryParseMode(host.Mode, out var mode))
            return Result<RhinoHostOptions>.Error(DbError.SystemFailure(new ArgumentException(
                $"Unknown Host.Mode '{host.Mode}' - expected run, replay, migrate, wal-prune, or wal-migrate.")));

        if (host.WalKeepGenerations is not null && host.WalPruneOlderThan is not null)
            return Result<RhinoHostOptions>.Error(DbError.SystemFailure(new ArgumentException(
                "Give either Host.WalKeepGenerations or Host.WalPruneOlderThan, not both - two retention policies at once is ambiguous.")));

        ulong? walPruneOlderThanUtcTicks = null;
        if (host.WalPruneOlderThan is { } pruneText) {
            var resolved = ResolveUtcTicks(pruneText, "Host.WalPruneOlderThan");
            if (resolved.IsError()) return resolved.Void();
            walPruneOlderThanUtcTicks = resolved.Unwrap();
        }

        NetworkTransportKind transport;
        ColdStoreSettings coldStore;
        TimeSpan shutdownBudget, exitWatchdog;
        try {
            transport = NetworkTransportParser.Parse((network ?? new NetworkConfig()).Transport);
            coldStore = ColdStoreSettings.FromConfig(durability ?? new DurabilityConfig());
            shutdownBudget = host.UnrecoverableError.ResolvedShutdownBudget;
            exitWatchdog = host.UnrecoverableError.ResolvedExitWatchdog;
        } catch (GeneratorConfigException ex) {
            return Result<RhinoHostOptions>.Error(DbError.SystemFailure(ex));
        }

        var coldPath = string.IsNullOrWhiteSpace(host.ColdPath) ? DefaultColdPath() : host.ColdPath;

        var accessResult = EnsureColdPathAccessible(coldPath);
        if (accessResult.IsError()) return Result<RhinoHostOptions>.Error(accessResult.GetError());

        return new RhinoHostOptions {
            ColdPath = coldPath,
            Mode = mode,
            ReplayUpToLsn = host.ReplayUpToLsn,
            WalKeepGenerations = host.WalKeepGenerations,
            WalPruneOlderThanUtcTicks = walPruneOlderThanUtcTicks,
            HttpPort = host.HttpPort,
            HttpEnabled = host.HttpEnabled ?? (mode == RhinoRunMode.Run),
            ServerVersion = serverVersion,
            Transport = transport,
            ColdStore = coldStore,
            UnrecoverableShutdownBudget = shutdownBudget,
            UnrecoverableExitWatchdog = exitWatchdog,
        };
    }

    static internal string DefaultColdPath() {
        var projectName = Assembly.GetEntryAssembly()?.GetName().Name ?? "RhinoDB";
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RhinoDB", projectName);
    }

    static internal Result EnsureColdPathAccessible(string coldPath) {
        try {
            Directory.CreateDirectory(coldPath);
            var probePath = Path.Combine(coldPath, $".rhinodb-access-check-{Guid.NewGuid():N}");
            File.WriteAllText(probePath, "");
            File.Delete(probePath);
            return Result.Ok();
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return Result.Error(DbError.SystemFailure(new InvalidOperationException(
                $"ColdPath '{coldPath}' is not accessible: {ex.Message}", ex)));
        }
    }

    static public Result<ulong> ResolveUtcTicks(string? value, string? flagName = null) {
        var label = flagName ?? "timestamp";
        if (string.IsNullOrWhiteSpace(value))
            return Result<ulong>.Error(DbError.SystemFailure(
                new ArgumentException($"{label} needs a value, e.g. 2026-09-14T08:00:00 (local), 2026-09-14T08:00:00Z (UTC), or 2026-09-14T08:00:00+02:00.")));

        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var when))
            return Result<ulong>.Error(DbError.SystemFailure(
                new ArgumentException($"{label} value '{value}' is not a recognizable timestamp. Use ISO-8601, e.g. 2026-09-14T08:00:00 (local), 2026-09-14T08:00:00Z (UTC), or 2026-09-14T08:00:00+02:00.")));

        return Result.Ok((ulong)when.UtcTicks);
    }

    static private bool TryParseMode(string? value, out RhinoRunMode mode) {
        switch (value) {
            case "run": mode = RhinoRunMode.Run; return true;
            case "replay": mode = RhinoRunMode.Replay; return true;
            case "migrate": mode = RhinoRunMode.Migrate; return true;
            case "wal-prune": mode = RhinoRunMode.WalPrune; return true;
            case "wal-migrate": mode = RhinoRunMode.WalMigrate; return true;
            default: mode = default; return false;
        }
    }
}
