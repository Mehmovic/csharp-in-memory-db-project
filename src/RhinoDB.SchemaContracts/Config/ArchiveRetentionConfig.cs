namespace RhinoDB.SchemaContracts;

public sealed class ArchiveRetentionConfig {
    public bool Enabled { get; set; }
    public string? Interval { get; set; }
    public string? KeepFor { get; set; }

    public TimeSpan? ResolvedInterval => Parse(Interval, "Server.ArchiveRetention.Interval");
    public TimeSpan? ResolvedKeepFor => Parse(KeepFor, "Server.ArchiveRetention.KeepFor");

    static private TimeSpan? Parse(string? value, string label) {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (TimeSpan.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        throw new GeneratorConfigException(
            $"{label} '{value}' is not a valid duration. Use [d.]hh:mm:ss, e.g. '04:00:00' for an interval or '30.00:00:00' for a 30-day window.");
    }
}