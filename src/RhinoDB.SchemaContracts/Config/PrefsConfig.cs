using System.Text.Json.Serialization;

namespace RhinoDB.SchemaContracts;

// RhinoPrefs (docs/manual/prefs.md): how long a value stays in memory after its last use, unless its Set overrides it.
public sealed class PrefsConfig {
    public string CacheDuration { get; set; } = "00:30:00";

    [JsonIgnore]
    public TimeSpan ResolvedCacheDuration => ConfigDuration.ParseRequired(CacheDuration, "Prefs.CacheDuration");
}
