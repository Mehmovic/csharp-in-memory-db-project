using RhinoDB.Core.Tables;

namespace RhinoDB.Run.Server.Benchmark.Schema;

[InstantTable(typeof(InstantBenchDb))]
public readonly partial record struct InstantWidget(
    [PrimaryKey] int Id,
    long Value
);
