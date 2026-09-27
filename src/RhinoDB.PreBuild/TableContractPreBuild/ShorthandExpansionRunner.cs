using RhinoDB.SchemaContracts;

namespace RhinoDB.PreBuild;

static public class ShorthandExpansionRunner {
    public sealed class Outcome(bool success, IReadOnlyList<string> errors, IReadOnlyList<string> writtenFiles) {
        public bool Success { get; } = success;
        public IReadOnlyList<string> Errors { get; } = errors;
        public IReadOnlyList<string> WrittenFiles { get; } = writtenFiles;
    }

    static public Outcome Generate(string projectDirectory) {
        GeneratorConfig config;
        ClientProtocolKind clientProtocol;
        try {
            config = GeneratorConfigLoader.Load(projectDirectory);
            clientProtocol = ClientProtocolParser.Parse(config.ClientProtocol);
        } catch (GeneratorConfigException ex) {
            return new Outcome(false, [ex.Message], []);
        }

        var contractsDir = Path.Combine(projectDirectory, config.SourceParentDirectory);
        var tablesSourceDir = Path.Combine(contractsDir, "Tables");
        var typesSourceDir = Path.Combine(contractsDir, "Types");
        Directory.CreateDirectory(tablesSourceDir);
        Directory.CreateDirectory(typesSourceDir);

        var allProjectSources = EnumerateProjectSourceFiles(projectDirectory).ToDictionary(f => f, File.ReadAllText);

        var targetRoot = Path.Combine(projectDirectory, config.TargetParentDirectory);
        var errors = new List<string>();
        var written = new List<string>();
        ExpandDirectory(tablesSourceDir, Path.Combine(targetRoot, "Tables"), allProjectSources, clientProtocol, errors, written);
        ExpandDirectory(typesSourceDir, Path.Combine(targetRoot, "Types"), allProjectSources, clientProtocol, errors, written);

        return new Outcome(errors.Count == 0, errors, written);
    }

    static public IReadOnlyList<string> Clean(string projectDirectory) {
        var config = GeneratorConfigLoader.Load(projectDirectory);
        var targetRoot = Path.Combine(projectDirectory, config.TargetParentDirectory);

        var deleted = new List<string>();
        foreach (var sub in new[] { "Tables", "Types" }) {
            var dir = Path.Combine(targetRoot, sub);
            if (!Directory.Exists(dir)) continue;

            foreach (var file in Directory.EnumerateFiles(dir, "*.g.cs", SearchOption.AllDirectories).ToList()) {
                File.Delete(file);
                deleted.Add(file);
            }

            RemoveEmptyDirectoriesDeepestFirst(dir);
        }

        if (Directory.Exists(targetRoot) && !Directory.EnumerateFileSystemEntries(targetRoot).Any())
            Directory.Delete(targetRoot);

        return deleted;
    }

    static private void RemoveEmptyDirectoriesDeepestFirst(string root) {
        if (!Directory.Exists(root)) return;

        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                     .OrderByDescending(d => d.Length)) {
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }

        if (!Directory.EnumerateFileSystemEntries(root).Any())
            Directory.Delete(root);
    }

    static private IEnumerable<string> EnumerateProjectSourceFiles(string projectDirectory) {
        var binDir = Path.Combine(projectDirectory, "bin") + Path.DirectorySeparatorChar;
        var objDir = Path.Combine(projectDirectory, "obj") + Path.DirectorySeparatorChar;

        return Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.StartsWith(binDir, StringComparison.OrdinalIgnoreCase) && !f.StartsWith(objDir, StringComparison.OrdinalIgnoreCase));
    }

    static private void ExpandDirectory(
        string sourceDir, string outputDir, IReadOnlyDictionary<string, string> allProjectSources,
        ClientProtocolKind clientProtocol, List<string> errors, List<string> written
    ) {
        if (!Directory.Exists(sourceDir)) return;

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*.cs", SearchOption.AllDirectories)) {
            try {
                var expanded = ShorthandExpander.Expand(File.ReadAllText(file), allProjectSources, clientProtocol);
                var outputPath = ComputeOutputPath(sourceDir, outputDir, file);
                if (WriteIfChanged(outputPath, expanded)) written.Add(outputPath);
            } catch (Exception ex) when (ex is ShorthandParseException or PackIdCollisionException or PackIdRangeException or ContractViolationException) {
                errors.Add($"{file}: {ex.Message}");
            }
        }
    }

    static public string ComputeOutputPath(string sourceDir, string outputDir, string sourceFile) {
        var normalizedSourceDir = sourceDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var relativePath = sourceFile.Substring(normalizedSourceDir.Length)
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var relativeDir = Path.GetDirectoryName(relativePath) ?? "";
        return Path.Combine(outputDir, relativeDir, Path.GetFileNameWithoutExtension(sourceFile) + ".g.cs");
    }

    static private bool WriteIfChanged(string path, string content) {
        if (File.Exists(path) && File.ReadAllText(path) == content) return false;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return true;
    }
}
