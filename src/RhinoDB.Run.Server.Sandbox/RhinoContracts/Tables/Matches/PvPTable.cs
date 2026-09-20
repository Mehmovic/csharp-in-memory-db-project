using RhinoDB.Core.Tables;

namespace RhinoDB.Run.Server.Sandbox;

[InstantTable(typeof(SandboxDb))]
public readonly partial record struct PvPTable(
    [PrimaryKey] int Id,
    int Score,
    [Index(IndexKind.BTree)] string username
);
