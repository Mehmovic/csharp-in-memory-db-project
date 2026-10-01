using System.Globalization;

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

    static public Result<RhinoHostOptions> Parse(string[] args, string prefix) {
        string? coldPath = null;
        var mode = RhinoRunMode.Run;
        ulong? upToLsn = null;
        int? walKeepGenerations = null;
        ulong? walPruneOlderThanUtcTicks = null;

        var coldPathFlag = $"--{prefix}.cold-path";
        var modeFlag = $"--{prefix}.mode";
        var upToLsnFlag = $"--{prefix}.replay-upto-lsn";
        var walKeepGenerationsFlag = $"--{prefix}.wal-keep-generations";
        var walPruneOlderThanFlag = $"--{prefix}.wal-prune-older-than";

        foreach (var arg in args) {
            var (key, value) = SplitFlag(arg);

            if (key == coldPathFlag) {
                coldPath = value;
            } else if (key == modeFlag) {
                if (!TryParseMode(value, out mode))
                    return Result<RhinoHostOptions>.Error(DbError.SystemFailure(
                        new ArgumentException($"Unknown {modeFlag} '{value}' - expected run, replay, migrate, wal-prune, or wal-migrate.")));
            } else if (key == upToLsnFlag) {
                if (!ulong.TryParse(value, out var lsn))
                    return Result<RhinoHostOptions>.Error(DbError.SystemFailure(
                        new ArgumentException($"{upToLsnFlag} must be an integer, got '{value}'.")));
                upToLsn = lsn;
            } else if (key == walKeepGenerationsFlag) {
                if (!int.TryParse(value, out var generation))
                    return Result<RhinoHostOptions>.Error(DbError.SystemFailure(
                        new ArgumentException($"{walKeepGenerationsFlag} must be an integer, got '{value}'.")));
                walKeepGenerations = generation;
            } else if (key == walPruneOlderThanFlag) {
                var resolved = ResolveUtcTicks(value, walPruneOlderThanFlag);
                if (resolved.IsError()) return resolved.Void();
                walPruneOlderThanUtcTicks = resolved.Unwrap();
            }
        }

        if (coldPath is null)
            return Result<RhinoHostOptions>.Error(DbError.SystemFailure(new ArgumentException($"{coldPathFlag} is required.")));

        if (walKeepGenerations is not null && walPruneOlderThanUtcTicks is not null)
            return Result<RhinoHostOptions>.Error(DbError.SystemFailure(new ArgumentException(
                $"Give either {walKeepGenerationsFlag} or {walPruneOlderThanFlag}, not both - two retention policies at once is ambiguous.")));

        return new RhinoHostOptions {
            ColdPath = coldPath, Mode = mode, ReplayUpToLsn = upToLsn,
            WalKeepGenerations = walKeepGenerations, WalPruneOlderThanUtcTicks = walPruneOlderThanUtcTicks
        };
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
    
    static private (string Key, string? Value) SplitFlag(string arg) {
        var eq = arg.IndexOf('=');
        return eq < 0 ? (arg, null) : (arg[..eq], arg[(eq + 1)..]);
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
