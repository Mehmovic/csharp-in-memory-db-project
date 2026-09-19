using MemoryPack;
using MessagePack;
using RhinoDB.Core.Tables;
using RhinoDB.Lib.Execution;

namespace RhinoDB.Run.Server.Benchmark.Schema;

[Database]
public partial class InstantBenchDb : DbContext<InstantBenchDbTransaction> { }

[Table(TableKind.Instant, typeof(InstantBenchDb))]
[MemoryPackable]
[MessagePackObject]
public readonly partial record struct InstantWidget(
    [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
    [property: MemoryPackOrder(1)] [property: Key(1)] long Value);
