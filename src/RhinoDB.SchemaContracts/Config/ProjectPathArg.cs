namespace RhinoDB.SchemaContracts;

static public class ProjectPathArg {
    private const string Flag = "--project";

    static public string? Resolve(string[] args, out string? error)
        => Resolve(args, out error, Directory.GetCurrentDirectory());

    static public string? Resolve(string[] args, out string? error, string startDirectory) {
        for (var i = 0; i < args.Length - 1; i++) {
            if (args[i] != Flag) continue;
            var explicitPath = args[i + 1];
            if (!File.Exists(explicitPath)) {
                error = $"{Flag} '{explicitPath}' does not exist.";
                return null;
            }
            error = null;
            return Path.GetFullPath(explicitPath);
        }

        for (var directory = new DirectoryInfo(startDirectory); directory is not null; directory = directory.Parent) {
            if (File.Exists(Path.Combine(directory.FullName, GeneratorConfigLoader.ConfigFileName))) {
                return ProjectFileIn(directory.FullName, out error);
            }
        }

        return ProjectFileIn(startDirectory, out error, walkedUp: true);
    }

    static private string? ProjectFileIn(string directory, out string? error, bool walkedUp = false) {
        var candidates = Directory.GetFiles(directory, "*.csproj");
        if (candidates.Length == 1) {
            error = null;
            return candidates[0];
        }

        error = candidates.Length == 0
            ? $"No .csproj found{(walkedUp ? ", and no " + GeneratorConfigLoader.ConfigFileName + " in this directory or any parent" : "")} - pass {Flag} <path>."
            : $"Multiple .csproj files found in {directory} ({string.Join(", ", candidates.Select(Path.GetFileName))}) - pass {Flag} <path> to disambiguate.";
        return null;
    }
}
