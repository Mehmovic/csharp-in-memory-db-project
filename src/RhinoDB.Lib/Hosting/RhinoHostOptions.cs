using System.Globalization;

namespace RhinoDB.Lib.Hosting;

public enum RhinoRunMode {
    Run,
    Replay,
    Migrate,
    Prune,
    ConsolidateArchive,
}

public sealed class RhinoHostOptions {
    public required string ColdPath { get; init; }
    public RhinoRunMode Mode { get; private init; } = RhinoRunMode.Run;
    public long? ReplayUpToLsn { get; private init; }
    public int? PruneTargetGeneration { get; private init; }
    public long? PruneOlderThanUtcTicks { get; private init; }

    static public Result<RhinoHostOptions> Parse(string[] args, string prefix) {
        string? coldPath = null;
        var mode = RhinoRunMode.Run;
        long? upToLsn = null;
        int? pruneTargetGeneration = null;
        long? pruneOlderThanUtcTicks = null;

        var coldPathFlag = $"--{prefix}.cold-path";
        var modeFlag = $"--{prefix}.mode";
        var upToLsnFlag = $"--{prefix}.replay-upto-lsn";
        var pruneTargetGenerationFlag = $"--{prefix}.prune-target-generation";
        var pruneOlderThanFlag = $"--{prefix}.prune-older-than";

        foreach (var arg in args) {
            var (key, value) = SplitFlag(arg);

            if (key == coldPathFlag) {
                coldPath = value;
            } else if (key == modeFlag) {
                if (!TryParseMode(value, out mode))
                    return Result<RhinoHostOptions>.Error(DbError.SystemFailure(
                        new ArgumentException($"Unknown {modeFlag} '{value}' - expected run, replay, migrate, prune, or consolidate-archive.")));
            } else if (key == upToLsnFlag) {
                if (!long.TryParse(value, out var lsn))
                    return Result<RhinoHostOptions>.Error(DbError.SystemFailure(
                        new ArgumentException($"{upToLsnFlag} must be an integer, got '{value}'.")));
                upToLsn = lsn;
            } else if (key == pruneTargetGenerationFlag) {
                if (!int.TryParse(value, out var generation))
                    return Result<RhinoHostOptions>.Error(DbError.SystemFailure(
                        new ArgumentException($"{pruneTargetGenerationFlag} must be an integer, got '{value}'.")));
                pruneTargetGeneration = generation;
            } else if (key == pruneOlderThanFlag) {
                var resolved = ResolveUtcTicks(value, pruneOlderThanFlag);
                if (resolved.IsError()) return resolved.Void();
                pruneOlderThanUtcTicks = resolved.Unwrap();
            }
        }

        if (coldPath is null)
            return Result<RhinoHostOptions>.Error(DbError.SystemFailure(new ArgumentException($"{coldPathFlag} is required.")));

        if (pruneTargetGeneration is not null && pruneOlderThanUtcTicks is not null)
            return Result<RhinoHostOptions>.Error(DbError.SystemFailure(new ArgumentException(
                $"Give either {pruneTargetGenerationFlag} or {pruneOlderThanFlag}, not both - two retention policies at once is ambiguous.")));

        return new RhinoHostOptions {
            ColdPath = coldPath, Mode = mode, ReplayUpToLsn = upToLsn,
            PruneTargetGeneration = pruneTargetGeneration, PruneOlderThanUtcTicks = pruneOlderThanUtcTicks
        };
    }

    static public Result<long> ResolveUtcTicks(string? value, string? flagName = null) {
        var label = flagName ?? "timestamp";
        if (string.IsNullOrWhiteSpace(value))
            return Result<long>.Error(DbError.SystemFailure(
                new ArgumentException($"{label} needs a value, e.g. 2026-09-14T08:00:00 (local), 2026-09-14T08:00:00Z (UTC), or 2026-09-14T08:00:00+02:00.")));

        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var when))
            return Result<long>.Error(DbError.SystemFailure(
                new ArgumentException($"{label} value '{value}' is not a recognizable timestamp. Use ISO-8601, e.g. 2026-09-14T08:00:00 (local), 2026-09-14T08:00:00Z (UTC), or 2026-09-14T08:00:00+02:00.")));

        return Result.Ok(when.UtcTicks);
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
            case "prune": mode = RhinoRunMode.Prune; return true;
            case "consolidate-archive": mode = RhinoRunMode.ConsolidateArchive; return true;
            default: mode = default; return false;
        }
    }
}
