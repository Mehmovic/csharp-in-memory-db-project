using MemoryPack;
using RhinoDB.Lib.Tables;

namespace RhinoDB.Lib.Durability;

[MemoryPackable]
public readonly partial struct WalChange(uint tableId, ChangeKind kind, byte[] key, byte[]? row) {
    public uint TableId { get; } = tableId;
    public ChangeKind Kind { get; } = kind;
    public byte[] Key { get; } = key;
    public byte[]? Row { get; } = row;
}
