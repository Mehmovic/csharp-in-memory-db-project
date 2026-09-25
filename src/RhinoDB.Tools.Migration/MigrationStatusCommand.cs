using Microsoft.CodeAnalysis.MSBuild;

using RhinoDB.PreBuild;
using RhinoDB.SchemaContracts;

namespace RhinoDB.Tools.Migration;

// `rhinodb migration status` - read-only. Loads the project, builds a fresh descriptor from its live
// compilation, diffs every table against the last committed Descriptor.json, and prints a per-table
// classification. Exits 1 if any table is Breaking, so it doubles as a CI gate (`rhinodb migration status
// || exit 1`) without needing a separate --check flag.
static public class MigrationStatusCommand {
    static public int Run(string[] args) {
        var projectPath = ProjectPathArg.Resolve(args, out var resolveError);
        if (resolveError is not null) {
            Console.Error.WriteLine(resolveError);
            return 1;
        }

        using var workspace = MSBuildWorkspace.Create();
        workspace.WorkspaceFailed += (_, e) => Console.Error.WriteLine($"warning: {e.Diagnostic.Message}");

        var project = workspace.OpenProjectAsync(projectPath!).GetAwaiter().GetResult();
        var compilation = project.GetCompilationAsync().GetAwaiter().GetResult();
        if (compilation is null) {
            Console.Error.WriteLine($"'{projectPath}' did not produce a compilation - is it a C# project?");
            return 1;
        }

        var newDescriptor = CompilationWalker.BuildDescriptor(compilation);

        var projectDirectory = Path.GetDirectoryName(projectPath!)!;
        var config = GeneratorConfigLoader.Load(projectDirectory);
        var descriptorPath = Path.Combine(projectDirectory, config.SchemaDescriptorPath);
        var oldDescriptor = File.Exists(descriptorPath) ? ContractDescriptorJson.Parse(File.ReadAllText(descriptorPath)) : null;

        if (oldDescriptor is null) {
            Console.WriteLine($"No committed descriptor found at '{descriptorPath}' - every table is new.");
            foreach (var table in newDescriptor.Tables)
                Console.WriteLine($"  {table.DatabaseFullName}.{table.Accessor}: Unchanged (new)");
            return 0;
        }

        var anyBreaking = false;
        var seenOldTables = new HashSet<(string DatabaseFullName, string Accessor)>();

        foreach (var newTable in newDescriptor.Tables) {
            var oldTable = oldDescriptor.Tables.FirstOrDefault(t => t.DatabaseFullName == newTable.DatabaseFullName && t.Accessor == newTable.Accessor);
            if (oldTable is not null) seenOldTables.Add((oldTable.DatabaseFullName, oldTable.Accessor));

            var classification = ContractDiff.Diff(oldTable, newTable);
            if (classification == DiffClassification.Breaking) anyBreaking = true;
            Console.WriteLine($"  {newTable.DatabaseFullName}.{newTable.Accessor}: {classification}");
        }

        foreach (var oldTable in oldDescriptor.Tables) {
            if (seenOldTables.Contains((oldTable.DatabaseFullName, oldTable.Accessor))) continue;
            Console.WriteLine($"  {oldTable.DatabaseFullName}.{oldTable.Accessor}: Removed");
        }

        return anyBreaking ? 1 : 0;
    }
}
