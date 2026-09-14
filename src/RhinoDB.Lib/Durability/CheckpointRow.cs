namespace RhinoDB.Lib.Durability;

public readonly struct CheckpointRow(uint tableId, byte[] key, byte[] row) {
    public uint TableId { get; } = tableId;
    public byte[] Key { get; } = key;
    public byte[] Row { get; } = row;
}
