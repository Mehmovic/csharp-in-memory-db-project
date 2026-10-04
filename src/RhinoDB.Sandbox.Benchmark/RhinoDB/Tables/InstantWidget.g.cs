using MemoryPack;
using MessagePack;
using RhinoDB.Core.Tables;

namespace RhinoDB.Sandbox.Benchmark.Schema;

[Table<InstantBenchDb>(TableKind.Instant)]
public readonly partial record struct InstantWidget(
    [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
    [property: MemoryPackOrder(1)] [property: Key(1)] long Value
);
