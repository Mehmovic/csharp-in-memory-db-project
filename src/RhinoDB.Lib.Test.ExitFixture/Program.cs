using System.Text.Json;

using RhinoDB.Core;

using RhinoDB.Lib.Cold;
using RhinoDB.Lib.Execution;
using RhinoDB.Lib.Hosting;
using RhinoDB.Lib.Tables;

namespace RhinoDB.Lib.Test.ExitFixture;

static internal class Program {
    private sealed class FixtureDb(ColdStore cold) : DbContext(cold);

    // Exit codes of its own: 0 would mean the fail-fast exit never happened; 2 = bad usage; 3 = startup failed.
    static private async Task<int> Main(string[] args) {
        if (args.Length != 1) return 2;
        var dir = args[0];
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "rdbsettings.json"),
            $$"""{ "Host": { "ColdPath": {{JsonSerializer.Serialize(dir)}}, "Mode": "run", "HttpEnabled": false, "HttpPort": 0 } }""");

        var built = await RhinoHostBuilder.Create(dir)
            .AddDatabase<FixtureDb, DefaultTransaction>(o => o.CreateDb = cold => new FixtureDb(cold))
            .BuildAsync();
        if (built.IsError()) return 3;
        var db = built.Unwrap().GetDatabase<FixtureDb>();

#if DEBUG
        db.Cold!.Wal.TestOnlyBeforeFlush = () => throw new IOException("injected fsync failure");
        _ = await db.RunConfirmed(ctx => {
            db.Cold!.Stage(1, ChangeKind.Insert, [1], [1]);
            return Result.Ok();
        });
#else
        db.PoisonDatabase(DbError.WalDurabilityFailed(new IOException("injected fsync failure")));
#endif

        await Task.Delay(TimeSpan.FromSeconds(30));
        return 0;
    }
}
