using RhinoDB.Core.Tables;

namespace RhinoDB.Sandbox;

[InstantTable<SandboxDb>]
public readonly partial record struct PvPTable(
    [PrimaryKey] int Id,
    int Score,
    [Index(IndexKind.BTree)] string username
);
