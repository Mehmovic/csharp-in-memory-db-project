using RhinoDB.Core.Tables;

namespace RhinoDB.Run.Server.Benchmark.Schema;

[PersistentTable(typeof(PersistentBenchDb))]
public readonly partial record struct PersistentWidget(
    [PrimaryKey] int Id,
    long Value
);
