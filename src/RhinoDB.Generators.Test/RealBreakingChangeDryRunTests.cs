using RhinoDB.Core;
using RhinoDB.Lib.Cold;
using RhinoDB.Lib.Execution;
using RhinoDB.Lib.Tables;
using RhinoDB.Sandbox.MigrationFixture;
using RhinoDB.SchemaContracts;

namespace RhinoDB.Generators.Test;

// Phase 5, step 25: the "first real breaking-change dry run" the plan calls for - see
// Fixtures/SchemaMigration/Generation0/README.md for exactly how these bytes were produced and why they
// must never be regenerated. Unlike every other test in this project, this one references
// RhinoDB.Sandbox.MigrationFixture as a REAL, ordinary ProjectReference rather than going through
// GeneratorTestHost's dynamic in-memory compilation - the whole point is exercising the actual, separately
// built project (real `dotnet build`, real `rhinodb migration create` CLI runs, a hand-written migration),
// not a per-test synthetic schema. LeagueDb/Player/LeagueDbLoader are therefore real, statically-typed
// classes here - no reflection needed anywhere in this file.
public class RealBreakingChangeDryRunTests {
    static private string FixtureBinPath([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(here)!, "Fixtures", "SchemaMigration", "Generation0", "Player.bin");

    static private List<(byte[] Key, byte[] Row)> ReadFrozenEntries(string path) {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        var count = reader.ReadInt32();
        var entries = new List<(byte[], byte[])>();
        for (var i = 0; i < count; i++) {
            var keyLen = reader.ReadInt32();
            var key = reader.ReadBytes(keyLen);
            var rowLen = reader.ReadInt32();
            var row = reader.ReadBytes(rowLen);
            entries.Add((key, row));
        }
        return entries;
    }

    [Test]
    public async Task FrozenGeneration0Bytes_MigrateCorrectlyThroughTheRealCompiledFixtureProject() {
        var entries = ReadFrozenEntries(FixtureBinPath());
        Assert.That(entries, Has.Count.EqualTo(3), "sanity: the frozen fixture must still contain exactly what it was captured with.");

        var dir = Path.Combine(Path.GetTempPath(), "rhinodb-real-breaking-change-dry-run", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try {
            // Seed generation-0 bytes directly into cold storage - these are the REAL bytes the V1 build of
            // this fixture project produced, before Rating ever existed. No migration has run yet.
            using (var seedCold = ColdStore.Open(dir).Unwrap()) {
                _ = new LeagueDb(seedCold);
                seedCold.BeginScope();
                foreach (var (key, row) in entries)
                    seedCold.Stage(TableIdHash.Compute("Player"), ChangeKind.Insert, key, row);
                Assert.That(await seedCold.EndScope(commit: true, PropagationMode.Confirmed, lsn: 1), Is.Null);
            }

            using var cold = ColdStore.Open(dir).Unwrap();
            var db = new LeagueDb(cold);
            Assert.That(cold.CompleteRecovery().IsOk(), Is.True);

            Assert.That(db.CurrentGeneration().Unwrap(), Is.EqualTo(0), "sanity: nothing has migrated yet.");

            var migrationResult = db.RunMigration();
            Assert.That(migrationResult.IsOk(), Is.True);
            Assert.That(db.CurrentGeneration().Unwrap(), Is.EqualTo(1), "RunMigration must bump the database to this build's G_binary.");

            var loader = new LeagueDbLoader();
            await loader.LoadAsync(db);

            var found = new bool[3];
            var ratings = new int[3];
            var names = new string[3];
            var result = await db.Run((ctx, tx) => {
                for (var i = 0; i < 3; i++) {
                    var id = i + 1;
                    var row = tx.Player.Primary.Find(id);
                    found[i] = row.HasRow();
                    if (found[i]) {
                        var value = row.Get().Unwrap();
                        ratings[i] = value.Rating;
                        names[i] = value.Name;
                    }
                }
                return Result.Ok();
            }, PropagationMode.Optimistic);
            Assert.That(result.IsOk(), Is.True);

            Assert.That(found, Is.All.True, "every frozen row must survive the migration.");
            Assert.That(ratings, Is.All.EqualTo(0), "Rating didn't exist in generation 0 - [Migration(FromRevision=0)] must default it to 0, not garbage or a misread byte.");
            Assert.That(names, Is.EqualTo(new[] { "Alice", "Bob", "Cara" }), "Id/Name must survive byte-for-byte through the real migration, unaffected by the field inserted before Name.");
        } finally {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
