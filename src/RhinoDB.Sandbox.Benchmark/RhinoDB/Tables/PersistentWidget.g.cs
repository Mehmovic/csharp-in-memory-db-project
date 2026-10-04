using MemoryPack;
using MessagePack;
using RhinoDB.Core.Tables;

namespace RhinoDB.Sandbox.Benchmark.Schema;

[Table<PersistentBenchDb>(TableKind.Persistent)]
public readonly partial record struct PersistentWidget(
    [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
    [property: MemoryPackOrder(1)] [property: Key(1)] long Value
);
