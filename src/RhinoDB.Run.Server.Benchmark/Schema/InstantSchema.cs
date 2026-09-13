using RhinoDB.Core.Tables;
using RhinoDB.Lib.Execution;

namespace RhinoDB.Run.Server.Benchmark.Schema;

[Database]
public partial class InstantBenchDb : DbContext<InstantBenchDbTransaction> { }

[Table(TableKind.Instant, typeof(InstantBenchDb))]
public readonly partial record struct InstantWidget([PrimaryKey] int Id, long Value);
