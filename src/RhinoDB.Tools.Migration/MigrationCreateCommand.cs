using Microsoft.CodeAnalysis.MSBuild;

using RhinoDB.PreBuild;
using RhinoDB.SchemaContracts;

namespace RhinoDB.Tools.Migration;

// `rhinodb migration create` - for every table whose Breaking change has no counterpart yet in the
// committed Descriptor.json: freezes the OLD shape as a real, compilable [FrozenSchema(Revision=N)] type,
// scaffolds an empty [Migration(FromRevision=N)] stub for the developer to fill in, and bumps
// Descriptor.json (the row type's own revision once, every owning database's generation once - grouped by
// row type, not by table, so a type shared across several tables/databases never gets double-bumped).
//
// v1 scope, deliberately not the full design: only Breaking changes are processed here. AdditiveOnly
// changes are NOT bumped by this command - the generator auto-synthesizes those at build time with no
// [Migration] required, and this command intentionally leaves their Descriptor.json revision/generation
// untouched rather than guessing whether "changed" should include them. A future pass can extend this once
// a real need for AdditiveOnly's own generation bump surfaces (point B's network-compatibility gate is the
// eventual consumer, not anything built yet).
static public class MigrationCreateCommand {
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
        var oldDescriptor = File.Exists(descriptorPath) ? ContractDescriptorJson.Parse(File.ReadAllText(descriptorPath)) : new DatabaseContractDescriptor();

        // CompilationWalker.BuildDescriptor always builds a FRESH DatabaseGenerationState per database
        // (Generation defaults to 0, since it has no access to the committed descriptor) - carry the real,
        // previously-persisted values forward before anything below increments them, or a second
        // `migration create` run against an already-migrated database would silently reset its generation
        // to 0 and increment from there instead of from its real value.
        foreach (var newDb in newDescriptor.Databases) {
            var oldDb = oldDescriptor.Databases.FirstOrDefault(d => d.FullName == newDb.FullName);
            if (oldDb is null) continue;
            newDb.Generation = oldDb.Generation;
            newDb.InvalidGenerations = oldDb.InvalidGenerations;
            newDb.RetainedFromGeneration = oldDb.RetainedFromGeneration;
        }

        var breakingByRowType = new Dictionary<string, List<(TableDescriptor Old, TableDescriptor New)>>();
        foreach (var newTable in newDescriptor.Tables) {
            var oldTable = oldDescriptor.Tables.FirstOrDefault(t => t.DatabaseFullName == newTable.DatabaseFullName && t.Accessor == newTable.Accessor);
            if (oldTable is null) continue;
            if (ContractDiff.Diff(oldTable, newTable) != DiffClassification.Breaking) continue;

            if (!breakingByRowType.TryGetValue(newTable.RowTypeFullName, out var pairs)) {
                pairs = [];
                breakingByRowType[newTable.RowTypeFullName] = pairs;
            }
            pairs.Add((oldTable, newTable));
        }

        if (breakingByRowType.Count == 0) {
            Console.WriteLine("No breaking changes detected - nothing to create.");
            return 0;
        }

        var migrationsDirectory = Path.Combine(projectDirectory, config.MigrationsOutputDirectory);
        Directory.CreateDirectory(migrationsDirectory);

        foreach (var (rowTypeFullName, pairs) in breakingByRowType) {
            var fromRevision = oldDescriptor.TypeRevisions.GetValueOrDefault(rowTypeFullName, 0);
            var toRevision = fromRevision + 1;
            var representative = pairs[0].Old;

            WriteFrozenSnapshot(migrationsDirectory, rowTypeFullName, fromRevision, representative);
            WriteMigrationStub(migrationsDirectory, rowTypeFullName, fromRevision, toRevision);

            newDescriptor.TypeRevisions[rowTypeFullName] = toRevision;

            // Bump every distinct affected database's generation FIRST, so each table below can record
            // its OWN database's post-increment value alongside the new revision - the runtime migration
            // engine's only way to answer "what revision is this table's on-disk data actually in?" given
            // nothing but G_db.
            var databaseGenerations = new Dictionary<string, int>();
            foreach (var databaseFullName in pairs.Select(p => p.New.DatabaseFullName).Distinct()) {
                var databaseState = newDescriptor.Databases.FirstOrDefault(d => d.FullName == databaseFullName);
                if (databaseState is null) {
                    databaseState = new DatabaseGenerationState { FullName = databaseFullName };
                    newDescriptor.Databases.Add(databaseState);
                }
                databaseState.Generation++;
                databaseGenerations[databaseFullName] = databaseState.Generation;
            }

            foreach (var (oldTable, newTable) in pairs) {
                newTable.Revision = toRevision;
                // Carry the OLD table's history forward (it doesn't exist on the freshly-built newTable at
                // all) before appending this run's own hop.
                newTable.RevisionHistory = new List<RevisionHistoryEntry>(oldTable.RevisionHistory) {
                    new RevisionHistoryEntry { Generation = databaseGenerations[newTable.DatabaseFullName], Revision = toRevision },
                };
            }

            Console.WriteLine($"  Created migration for '{rowTypeFullName}': revision {fromRevision} -> {toRevision}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(descriptorPath)!);
        File.WriteAllText(descriptorPath, ContractDescriptorJson.Serialize(newDescriptor));
        return 0;
    }

    static void WriteFrozenSnapshot(string migrationsDirectory, string rowTypeFullName, int fromRevision, TableDescriptor oldTable) {
        var (@namespace, simpleName) = SplitFullName(rowTypeFullName);
        var frozenTypeName = $"{simpleName}_Rev{fromRevision}";
        var frozenNamespace = @namespace is null ? $"SchemaHistory.Revision{fromRevision}" : $"{@namespace}.SchemaHistory.Revision{fromRevision}";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("// <auto-generated>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using RhinoDB.Core.Tables;");
        sb.AppendLine();
        sb.AppendLine($"namespace {frozenNamespace};");
        sb.AppendLine();
        sb.AppendLine($"[FrozenSchema({fromRevision})]");
        sb.Append($"public readonly partial record struct {frozenTypeName}(");
        sb.Append(string.Join(", ", oldTable.Fields.Select(f => {
            var identifier = SanitizeIdentifier(f.Path);
            var primaryKeyPrefix = f.Path == oldTable.PrimaryKey.Path ? "[PrimaryKey] " : "";
            return $"{primaryKeyPrefix}{f.TypeFullName} {identifier}";
        })));
        sb.AppendLine(");");

        var path = Path.Combine(migrationsDirectory, $"{frozenTypeName}.g.cs");
        var content = sb.ToString();
        if (!File.Exists(path) || File.ReadAllText(path) != content) File.WriteAllText(path, content);
    }

    static void WriteMigrationStub(string migrationsDirectory, string rowTypeFullName, int fromRevision, int toRevision) {
        var (@namespace, simpleName) = SplitFullName(rowTypeFullName);
        var frozenTypeName = $"{simpleName}_Rev{fromRevision}";
        var frozenNamespace = @namespace is null ? $"SchemaHistory.Revision{fromRevision}" : $"{@namespace}.SchemaHistory.Revision{fromRevision}";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("using System;");
        sb.AppendLine();
        if (@namespace is not null) {
            sb.AppendLine($"namespace {@namespace};");
            sb.AppendLine();
        }
        sb.AppendLine($"public readonly partial record struct {simpleName} {{");
        sb.AppendLine($"    [RhinoDB.Core.Tables.Migration({fromRevision})]");
        sb.AppendLine($"    internal static {simpleName} FromRevision{fromRevision}({frozenNamespace}.{frozenTypeName} old) {{");
        sb.AppendLine($"        throw new NotImplementedException(\"TODO: implement the migration from revision {fromRevision} to {toRevision} for {simpleName}.\");");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        var path = Path.Combine(migrationsDirectory, $"{simpleName}_FromRev{fromRevision}.cs");
        var freshContent = sb.ToString();
        if (File.Exists(path) && File.ReadAllText(path) != freshContent) {
            Console.WriteLine($"  '{path}' already exists and was hand-edited - not overwriting.");
            return;
        }
        File.WriteAllText(path, freshContent);
    }

    static string SanitizeIdentifier(string dottedPath) => dottedPath.Replace('.', '_');

    static (string? Namespace, string SimpleName) SplitFullName(string fullyQualifiedName) {
        var stripped = fullyQualifiedName.StartsWith("global::") ? fullyQualifiedName[8..] : fullyQualifiedName;
        var lastDot = stripped.LastIndexOf('.');
        return lastDot < 0 ? (null, stripped) : (stripped[..lastDot], stripped[(lastDot + 1)..]);
    }
}
