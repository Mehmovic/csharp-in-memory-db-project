namespace RhinoDB.Lib.Hosting;

public enum RhinoRunMode {
    Run,
    Replay,
    Migrate,
}

public sealed class RhinoHostOptions {
    public required string ColdPath { get; init; }
    public RhinoRunMode Mode { get; private init; } = RhinoRunMode.Run;
    public long? ReplayUpToLsn { get; private init; }

    static public Result<RhinoHostOptions> Parse(string[] args, string prefix) {
        string? coldPath = null;
        var mode = RhinoRunMode.Run;
        long? upToLsn = null;

        var coldPathFlag = $"--{prefix}.cold-path";
        var modeFlag = $"--{prefix}.mode";
        var upToLsnFlag = $"--{prefix}.replay-upto-lsn";

        foreach (var arg in args) {
            var (key, value) = SplitFlag(arg);

            if (key == coldPathFlag) {
                coldPath = value;
            } else if (key == modeFlag) {
                if (!TryParseMode(value, out mode))
                    return Result<RhinoHostOptions>.Error(DbError.SystemFailure(
                        new ArgumentException($"Unknown {modeFlag} '{value}' - expected run, replay, or migrate.")));
            } else if (key == upToLsnFlag) {
                if (!long.TryParse(value, out var lsn))
                    return Result<RhinoHostOptions>.Error(DbError.SystemFailure(
                        new ArgumentException($"{upToLsnFlag} must be an integer, got '{value}'.")));
                upToLsn = lsn;
            }
        }

        if (coldPath is null)
            return Result<RhinoHostOptions>.Error(DbError.SystemFailure(new ArgumentException($"{coldPathFlag} is required.")));

        return new RhinoHostOptions { ColdPath = coldPath, Mode = mode, ReplayUpToLsn = upToLsn };
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
            default: mode = default; return false;
        }
    }
}
