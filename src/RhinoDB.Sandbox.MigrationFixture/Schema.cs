using MemoryPack;
using MessagePack;
using RhinoDB.Core.Tables;
using RhinoDB.Lib.Execution;

namespace RhinoDB.Sandbox.MigrationFixture;

[Database]
public partial class LeagueDb : DbContext<LeagueDbTransaction> { }

[Table(TableKind.Persistent, typeof(LeagueDb))]
[MemoryPackable(GenerateType.VersionTolerant)]
[MessagePackObject]
public readonly partial record struct Player(
    [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
    [property: MemoryPackOrder(1)] [property: Key(1)] int Rating,
    [property: MemoryPackOrder(2)] [property: Key(2)] string Name);
 