using Microsoft.Build.Framework;

using RhinoDB.SchemaContracts;

namespace RhinoDB.PreBuild;

public sealed class ExpandShorthandTask : Microsoft.Build.Utilities.Task {
    [Required]
    public string ProjectDirectory { get; set; } = "";

    public override bool Execute() {
        GeneratorConfig config;
        ClientProtocolKind clientProtocol;
        try {
            config = GeneratorConfigLoader.Load(ProjectDirectory);
            clientProtocol = ClientProtocolParser.Parse(config.ClientProtocol);
        } catch (GeneratorConfigException ex) {
            Log.LogError(ex.Message);
            return false;
        }

        var contractsDir = Path.Combine(ProjectDirectory, config.SourceParentDirectory);
        var tablesSourceDir = Path.Combine(contractsDir, "Tables");
        var typesSourceDir = Path.Combine(contractsDir, "Types");
        Directory.CreateDirectory(tablesSourceDir);
        Directory.CreateDirectory(typesSourceDir);

        var allProjectSources = EnumerateProjectSourceFiles(ProjectDirectory)
            .ToDictionary(f => f, File.ReadAllText);

        var targetRoot = Path.Combine(ProjectDirectory, config.TargetParentDirectory);
        var tablesOk = ExpandDirectory(tablesSourceDir, Path.Combine(targetRoot, "Tables"), allProjectSources, clientProtocol);
        var typesOk = ExpandDirectory(typesSourceDir, Path.Combine(targetRoot, "Types"), allProjectSources, clientProtocol);
        return tablesOk && typesOk;
    }

    static private IEnumerable<string> EnumerateProjectSourceFiles(string projectDirectory) {
        var binDir = Path.Combine(projectDirectory, "bin") + Path.DirectorySeparatorChar;
        var objDir = Path.Combine(projectDirectory, "obj") + Path.DirectorySeparatorChar;

        return Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.StartsWith(binDir, StringComparison.OrdinalIgnoreCase) && !f.StartsWith(objDir, StringComparison.OrdinalIgnoreCase));
    }

    private bool ExpandDirectory(string sourceDir, string outputDir, IReadOnlyDictionary<string, string> allProjectSources, ClientProtocolKind clientProtocol) {
        if (!Directory.Exists(sourceDir)) return true;

        var success = true;
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*.cs", SearchOption.AllDirectories)) {
            try {
                var expanded = ShorthandExpander.Expand(File.ReadAllText(file), allProjectSources, clientProtocol);
                var outputPath = ComputeOutputPath(sourceDir, outputDir, file);
                WriteIfChanged(outputPath, expanded);
            } catch (Exception ex) when (ex is ShorthandParseException or PackIdCollisionException or PackIdRangeException or ContractViolationException) {
                Log.LogError($"{file}: {ex.Message}");
                success = false;
            }
        }

        return success;
    }

    static public string ComputeOutputPath(string sourceDir, string outputDir, string sourceFile) {
        var normalizedSourceDir = sourceDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var relativePath = sourceFile.Substring(normalizedSourceDir.Length)
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var relativeDir = Path.GetDirectoryName(relativePath) ?? "";
        return Path.Combine(outputDir, relativeDir, Path.GetFileNameWithoutExtension(sourceFile) + ".g.cs");
    }

    static private void WriteIfChanged(string path, string content) {
        if (File.Exists(path) && File.ReadAllText(path) == content) return;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
