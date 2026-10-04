using RhinoDB.Core.Tables;

namespace RhinoDB.Sandbox.Benchmark.Schema;

[PersistentTable<PersistentBenchDb>]
public readonly partial record struct PersistentWidget(
    [PrimaryKey] int Id,
    long Value
);
