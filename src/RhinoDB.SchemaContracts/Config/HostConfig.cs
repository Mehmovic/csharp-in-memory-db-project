namespace RhinoDB.SchemaContracts;

public sealed class HostConfig {
    public string? ColdPath { get; set; }
    public string Mode { get; set; } = "run";
    public ulong? ReplayUpToLsn { get; set; }
    public int? WalKeepGenerations { get; set; }
    public string? WalPruneOlderThan { get; set; }
    public int HttpPort { get; set; } = 7777;
    public bool? HttpEnabled { get; set; }
}
