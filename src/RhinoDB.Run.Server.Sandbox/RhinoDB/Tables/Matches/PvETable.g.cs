using MemoryPack;
using MessagePack;
using RhinoDB.Core.Tables;

namespace RhinoDB.Run.Server.Sandbox;

[Table(TableKind.Instant, typeof(SandboxDb))]
[MemoryPackable]
[MessagePackObject]
public readonly partial record struct PvETable(
    [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
    [property: MemoryPackOrder(1)] [property: Key(1)] int WaveNumber
);
