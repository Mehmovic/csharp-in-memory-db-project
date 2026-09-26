using System.Text.RegularExpressions;

using RhinoDB.PreBuild;
using RhinoDB.SchemaContracts;

namespace RhinoDB.Tools.Migration;

static public class MigrationPruneHistoryCommand {
    static private readonly Regex SnapshotFilePattern = new(@"^(?<name>.+)_Rev(?<revision>\d+)\.g\.cs$");
    static private readonly Regex StubFilePattern = new(@"^(?<name>.+)_FromRev(?<revision>\d+)\.cs$");

    static public int Run(string[] args) {
        var projectPath = ProjectPathArg.Resolve(args, out var resolveError);
        if (resolveError is not null) {
            Console.Error.WriteLine(resolveError);
            return 1;
        }

        var projectDirectory = Path.GetDirectoryName(projectPath!)!;
        var config = GeneratorConfigLoader.Load(projectDirectory);
        var descriptorPath = Path.Combine(projectDirectory, config.SchemaDescriptorPath);
        if (!File.Exists(descriptorPath)) {
            Console.WriteLine("No committed Descriptor.json found - nothing to prune.");
            return 0;
        }

        var descriptor = ContractDescriptorJson.Parse(File.ReadAllText(descriptorPath));
        var safeFloorRevisionBySimpleName = BuildSafeFloorRevisions(descriptor);

        var migrationsDirectory = Path.Combine(projectDirectory, config.MigrationsOutputDirectory);
        if (!Directory.Exists(migrationsDirectory)) {
            Console.WriteLine("No migrations directory found - nothing to prune.");
            return 0;
        }

        var anyAction = false;
        foreach (var path in Directory.EnumerateFiles(migrationsDirectory, "*.cs").OrderBy(p => p, StringComparer.Ordinal)) {
            var fileName = Path.GetFileName(path);

            var snapshotMatch = SnapshotFilePattern.Match(fileName);
            if (snapshotMatch.Success) {
                var simpleName = snapshotMatch.Groups["name"].Value;
                var revision = int.Parse(snapshotMatch.Groups["revision"].Value);
                if (!safeFloorRevisionBySimpleName.TryGetValue(simpleName, out var floor) || revision >= floor) continue;

                File.Delete(path);
                Console.WriteLine($"  Deleted dead frozen snapshot '{fileName}' (revision {revision} is below every referencing table's retained floor).");
                anyAction = true;
                continue;
            }

            var stubMatch = StubFilePattern.Match(fileName);
            if (stubMatch.Success) {
                var simpleName = stubMatch.Groups["name"].Value;
                var revision = int.Parse(stubMatch.Groups["revision"].Value);
                if (!safeFloorRevisionBySimpleName.TryGetValue(simpleName, out var floor) || revision >= floor) continue;

                Console.WriteLine($"  '{fileName}' is no longer required (revision {revision} is below every referencing table's retained floor) - safe to remove by hand.");
                anyAction = true;
            }
        }

        if (!anyAction) Console.WriteLine("Nothing to prune - every frozen snapshot and migration stub may still be needed.");
        return 0;
    }

    static private Dictionary<string, int> BuildSafeFloorRevisions(DatabaseContractDescriptor descriptor) {
        var result = new Dictionary<string, int>();

        foreach (var table in descriptor.Tables) {
            var database = descriptor.Databases.FirstOrDefault(d => d.FullName == table.DatabaseFullName);
            var retainedFromGeneration = database?.RetainedFromGeneration ?? 0;

            var floorRevision = table.RevisionHistory
                .Where(h => h.Generation <= retainedFromGeneration)
                .OrderBy(h => h.Generation)
                .Select(h => (int?)h.Revision)
                .LastOrDefault() ?? 0;

            var (_, simpleName) = SplitFullName(table.RowTypeFullName);
            result[simpleName] = result.TryGetValue(simpleName, out var existing) ? Math.Min(existing, floorRevision) : floorRevision;
        }

        return result;
    }

    static (string? Namespace, string SimpleName) SplitFullName(string fullyQualifiedName) {
        var stripped = fullyQualifiedName.StartsWith("global::") ? fullyQualifiedName[8..] : fullyQualifiedName;
        var lastDot = stripped.LastIndexOf('.');
        return lastDot < 0 ? (null, stripped) : (stripped[..lastDot], stripped[(lastDot + 1)..]);
    }
}
