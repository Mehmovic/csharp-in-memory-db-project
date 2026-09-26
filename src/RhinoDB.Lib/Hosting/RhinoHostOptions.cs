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

    static public Result<RhinoHostOptions> Parse(string[] args, string prefix) {
        string? coldPath = null;
        var mode = RhinoRunMode.Run;
        long? upToLsn = null;
        int? pruneTargetGeneration = null;

        var coldPathFlag = $"--{prefix}.cold-path";
        var modeFlag = $"--{prefix}.mode";
        var upToLsnFlag = $"--{prefix}.replay-upto-lsn";
        var pruneTargetGenerationFlag = $"--{prefix}.prune-target-generation";

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
            }
        }

        if (coldPath is null)
            return Result<RhinoHostOptions>.Error(DbError.SystemFailure(new ArgumentException($"{coldPathFlag} is required.")));

        return new RhinoHostOptions { ColdPath = coldPath, Mode = mode, ReplayUpToLsn = upToLsn, PruneTargetGeneration = pruneTargetGeneration };
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
