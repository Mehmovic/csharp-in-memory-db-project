using RhinoDB.Core.Tables;

namespace RhinoDB.Sandbox.Benchmark.Schema;

[PersistentTable<MatchDb>]
public readonly partial record struct MatchRecord(
    [PrimaryKey] int Id,
    long Score
);
