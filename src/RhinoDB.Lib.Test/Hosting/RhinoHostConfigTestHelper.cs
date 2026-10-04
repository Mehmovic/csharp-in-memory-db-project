using System.Text.Json;

using RhinoDB.SchemaContracts;

namespace RhinoDB.Lib.Hosting.Test;

static internal class RhinoHostConfigTestHelper {
    static private readonly JsonSerializerOptions Option = new JsonSerializerOptions { WriteIndented = true };
    
    static public string WriteConfig(string directory, HostConfig host, ServerConfig? server = null) {
        var json = JsonSerializer.Serialize(new RhinoDbConfig { Host = host, Server = server ?? new ServerConfig() }, Option);
        File.WriteAllText(Path.Combine(directory, GeneratorConfigLoader.ConfigFileName), json);
        return directory;
    }
}
