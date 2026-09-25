namespace RhinoDB.Tools.Migration;

static public class ProjectPathArg {
    // --project <path> if given; otherwise the single .csproj in the current directory. Ambiguous (0 or
    // 2+ candidates) with no explicit --project is a clear error, not a guess.
    static public string? Resolve(string[] args, out string? error) {
        for (var i = 0; i < args.Length - 1; i++) {
            if (args[i] != "--project") continue;
            var explicitPath = args[i + 1];
            if (!File.Exists(explicitPath)) {
                error = $"--project '{explicitPath}' does not exist.";
                return null;
            }
            error = null;
            return Path.GetFullPath(explicitPath);
        }

        var candidates = Directory.GetFiles(Directory.GetCurrentDirectory(), "*.csproj");
        if (candidates.Length == 1) {
            error = null;
            return candidates[0];
        }

        error = candidates.Length == 0
            ? "No .csproj found in the current directory - pass --project <path>."
            : $"Multiple .csproj files found in the current directory ({string.Join(", ", candidates.Select(Path.GetFileName))}) - pass --project <path> to disambiguate.";
        return null;
    }
}
