using System.Text.Json.Serialization;

namespace RhinoDB.SchemaContracts;

// Timings of the fail-fast exit (docs/manual/operations.md). The policy itself (exit vs callback) stays code-only.
public sealed class UnrecoverableErrorConfig {
    public string ShutdownBudget { get; set; } = "00:00:05";
    public string ExitWatchdog { get; set; } = "00:00:07";

    [JsonIgnore]
    public TimeSpan ResolvedShutdownBudget => ConfigDuration.ParseRequired(ShutdownBudget, "Host.UnrecoverableError.ShutdownBudget");

    [JsonIgnore]
    public TimeSpan ResolvedExitWatchdog => ConfigDuration.ParseRequired(ExitWatchdog, "Host.UnrecoverableError.ExitWatchdog");
}
