namespace RhinoDB.Sandbox.MigrationFixture;

public readonly partial record struct Player {
    [RhinoDB.Core.Tables.Migration(0)]
    internal static Player FromRevision0(RhinoDB.Sandbox.MigrationFixture.SchemaHistory.Revision0.Player_Rev0 old) =>
        new Player(old.Id, Rating: 0, old.Name);
}
