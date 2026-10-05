using RhinoDB.Core.Tables;

namespace RhinoDB.Sandbox.Benchmark.Schema;

[PersistentTable<HostRootDb>]
public readonly partial record struct HostWallet(
    [PrimaryKey] int Id,
    long Coins
);
