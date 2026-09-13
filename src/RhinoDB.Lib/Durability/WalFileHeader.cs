namespace RhinoDB.Lib.Durability;

public readonly struct WalFileHeader(uint formatVersion, Guid databaseId) {
    public uint FormatVersion { get; } = formatVersion;
    public Guid DatabaseId { get; } = databaseId;
}
