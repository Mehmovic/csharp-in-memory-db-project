using System.Text.Json;

namespace RhinoDB.SchemaContracts;

static public class GeneratorConfigLoader {
    public const string ConfigFileName = "rdbsettings.json";

    static public GeneratorConfig Load(string projectDirectory) => LoadFull(projectDirectory).Generator;

    static public RhinoDbConfig LoadFull(string projectDirectory) {
        var configPath = Path.Combine(projectDirectory, ConfigFileName);
        if (!File.Exists(configPath)) {
            var defaultConfig = new RhinoDbConfig();
            TryWriteDefaultFile(configPath, defaultConfig);
            return defaultConfig;
        }

        return ParseFull(File.ReadAllText(configPath));
    }

    static private void TryWriteDefaultFile(string configPath, RhinoDbConfig defaultConfig) {
        try {
            var json = JsonSerializer.Serialize(defaultConfig, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(configPath, json);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
        }
    }

    static public GeneratorConfig Parse(string json) => ParseFull(json).Generator;

    static public RhinoDbConfig ParseFull(string json) {
        try {
            return JsonSerializer.Deserialize<RhinoDbConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new GeneratorConfigException($"'{ConfigFileName}' parsed to null - it must be a JSON object.");
        } catch (JsonException ex) {
            throw new GeneratorConfigException($"'{ConfigFileName}' is not valid JSON: {ex.Message}", ex);
        }
    }
}
