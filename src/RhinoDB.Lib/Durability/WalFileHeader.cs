namespace RhinoDB.Lib.Durability;

public readonly struct WalFileHeader(uint formatVersion, Guid databaseId, uint generation) {
    public uint FormatVersion { get; } = formatVersion;
    public Guid DatabaseId { get; } = databaseId;
    public uint Generation { get; } = generation;
}
