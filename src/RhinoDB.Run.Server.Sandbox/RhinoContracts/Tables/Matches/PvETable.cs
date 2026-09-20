using RhinoDB.Core.Tables;

namespace RhinoDB.Run.Server.Sandbox;

[InstantTable(typeof(SandboxDb))]
public readonly partial record struct PvETable(
    [PrimaryKey] int Id,
    int WaveNumber
);
