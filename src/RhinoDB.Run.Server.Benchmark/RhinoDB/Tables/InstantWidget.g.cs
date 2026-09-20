using MemoryPack;
using MessagePack;
using RhinoDB.Core.Tables;

namespace RhinoDB.Run.Server.Benchmark.Schema;

[Table(TableKind.Instant, typeof(InstantBenchDb))]
[MemoryPackable]
[MessagePackObject]
public readonly partial record struct InstantWidget(
    [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
    [property: MemoryPackOrder(1)] [property: Key(1)] long Value
);
