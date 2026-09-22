using System.Text.Json;
using System.Text.Json.Serialization;

namespace RhinoDB.SchemaContracts;

static public class ContractDescriptorJson {
    static readonly JsonSerializerOptions Options = new() {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    static public string Serialize(DatabaseContractDescriptor descriptor) =>
        JsonSerializer.Serialize(descriptor, Options);

    static public DatabaseContractDescriptor Parse(string json) {
        try {
            return JsonSerializer.Deserialize<DatabaseContractDescriptor>(json, Options)
                ?? throw new SchemaContractDescriptorException("Descriptor.json parsed to null - it must be a JSON object.");
        } catch (JsonException ex) {
            throw new SchemaContractDescriptorException($"Descriptor.json is not valid JSON: {ex.Message}", ex);
        }
    }
}
