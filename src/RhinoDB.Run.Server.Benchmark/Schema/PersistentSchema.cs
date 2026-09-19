using MemoryPack;
using MessagePack;
using RhinoDB.Core.Tables;
using RhinoDB.Lib.Execution;

namespace RhinoDB.Run.Server.Benchmark.Schema;

[Database]
public partial class PersistentBenchDb : DbContext<PersistentBenchDbTransaction> { }

[Table(TableKind.Persistent, typeof(PersistentBenchDb))]
[MemoryPackable]
[MessagePackObject]
public readonly partial record struct PersistentWidget(
    [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
    [property: MemoryPackOrder(1)] [property: Key(1)] long Value);
