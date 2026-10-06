using System.Globalization;

namespace RhinoDB.SchemaContracts;

static public class ConfigDuration {
    static public TimeSpan? Parse(string? value, string label) {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed) && parsed > TimeSpan.Zero) return parsed;
        throw new GeneratorConfigException(
            $"{label} '{value}' is not a valid positive duration. Use [d.]hh:mm:ss[.fff], e.g. '00:00:05' for five seconds or '30.00:00:00' for 30 days.");
    }

    static public TimeSpan ParseRequired(string? value, string label) =>
        Parse(value, label) ?? throw new GeneratorConfigException($"{label} needs a value, e.g. '00:00:05'.");
}
