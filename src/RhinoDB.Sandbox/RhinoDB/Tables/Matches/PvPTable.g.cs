using MemoryPack;
using MessagePack;
using RhinoDB.Core.Tables;

namespace RhinoDB.Sandbox;

[Table(TableKind.Instant, typeof(SandboxDb))]
public readonly partial record struct PvPTable(
    [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
    [property: MemoryPackOrder(1)] [property: Key(1)] int Score,
    [Index(IndexKind.BTree)] [property: MemoryPackOrder(2)] [property: Key(2)] string username
);
