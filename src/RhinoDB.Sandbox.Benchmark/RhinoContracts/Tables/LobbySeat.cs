using RhinoDB.Core.Tables;

namespace RhinoDB.Sandbox.Benchmark.Schema;

[InstantTable<LobbyDb>]
public readonly partial record struct LobbySeat(
    [PrimaryKey] int Id,
    long PlayerId
);
