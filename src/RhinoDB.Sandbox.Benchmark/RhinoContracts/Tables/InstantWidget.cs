using RhinoDB.Core.Tables;

namespace RhinoDB.Sandbox.Benchmark.Schema;

[InstantTable<InstantBenchDb>]
public readonly partial record struct InstantWidget(
    [PrimaryKey] int Id,
    long Value
);
