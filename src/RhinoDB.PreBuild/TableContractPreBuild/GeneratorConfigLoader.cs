using System.Text.Json;

namespace RhinoDB.PreBuild;

static public class GeneratorConfigLoader {
    public const string ConfigFileName = "config.Generator.json";

    static public GeneratorConfig Load(string projectDirectory) {
        var configPath = Path.Combine(projectDirectory, ConfigFileName);
        if (!File.Exists(configPath)) {
            var defaultConfig = new GeneratorConfig();
            TryWriteDefaultFile(configPath, defaultConfig);
            return defaultConfig;
        }

        return Parse(File.ReadAllText(configPath));
    }

    // Best-effort - a developer editing this file later is the point (real, visible, editable
    // settings instead of an invisible in-memory fallback), but failing to scaffold it must never
    // fail the build, since the tool works fine without it either way.
    static void TryWriteDefaultFile(string configPath, GeneratorConfig defaultConfig) {
        try {
            var json = JsonSerializer.Serialize(defaultConfig, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(configPath, json);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
        }
    }

    static public GeneratorConfig Parse(string json) {
        try {
            return JsonSerializer.Deserialize<GeneratorConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new GeneratorConfigException($"'{ConfigFileName}' parsed to null - it must be a JSON object.");
        } catch (JsonException ex) {
            throw new GeneratorConfigException($"'{ConfigFileName}' is not valid JSON: {ex.Message}", ex);
        }
    }
}
