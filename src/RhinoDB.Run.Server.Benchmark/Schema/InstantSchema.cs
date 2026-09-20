using RhinoDB.Core.Tables;
using RhinoDB.Lib.Execution;

namespace RhinoDB.Run.Server.Benchmark.Schema;

[Database]
public partial class InstantBenchDb : DbContext<InstantBenchDbTransaction> { }
