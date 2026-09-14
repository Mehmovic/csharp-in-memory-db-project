namespace RhinoDB.Lib.Durability;

public readonly struct EvictionCandidate(uint tableId, byte[] key) {
    public uint TableId { get; } = tableId;
    public byte[] Key { get; } = key;
}
