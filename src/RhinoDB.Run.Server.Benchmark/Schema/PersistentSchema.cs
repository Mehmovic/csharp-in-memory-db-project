using RhinoDB.Core.Tables;
using RhinoDB.Lib.Execution;

namespace RhinoDB.Run.Server.Benchmark.Schema;

[Database]
public partial class PersistentBenchDb : DbContext<PersistentBenchDbTransaction> { }

[Table(TableKind.Persistent, typeof(PersistentBenchDb))]
public readonly partial record struct PersistentWidget([PrimaryKey] int Id, long Value);
