using RhinoDB.Core.Tables;
using RhinoDB.Lib.Execution;

namespace RhinoDB.Sandbox.Benchmark.Schema;

[Database]
public partial class PersistentBenchDb : DbContext<PersistentBenchDbTransaction> { }
