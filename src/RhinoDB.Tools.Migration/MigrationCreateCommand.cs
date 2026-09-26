using Microsoft.CodeAnalysis.MSBuild;

using RhinoDB.PreBuild;
using RhinoDB.SchemaContracts;

namespace RhinoDB.Tools.Migration;

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
        var descriptorExistedBefore = File.Exists(descriptorPath);
        var oldDescriptor = descriptorExistedBefore ? ContractDescriptorJson.Parse(File.ReadAllText(descriptorPath)) : new DatabaseContractDescriptor();

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

        var newlyOrphaned = new List<TableDescriptor>();
        foreach (var oldTable in oldDescriptor.Tables) {
            if (oldTable.RemovedAtGeneration is not null) {
                newDescriptor.Tables.Add(oldTable);
                continue;
            }
            var stillLive = newDescriptor.Tables.Any(t => t.DatabaseFullName == oldTable.DatabaseFullName && t.Accessor == oldTable.Accessor);
            if (stillLive) continue;

            var owningDatabase = newDescriptor.Databases.FirstOrDefault(d => d.FullName == oldTable.DatabaseFullName);
            oldTable.RemovedAtGeneration = owningDatabase?.Generation ?? 0;
            newDescriptor.Tables.Add(oldTable);
            newlyOrphaned.Add(oldTable);
        }

        if (breakingByRowType.Count == 0 && newlyOrphaned.Count == 0) {
            if (!descriptorExistedBefore) {
                Directory.CreateDirectory(Path.GetDirectoryName(descriptorPath)!);
                File.WriteAllText(descriptorPath, ContractDescriptorJson.Serialize(newDescriptor));
                Console.WriteLine("No committed schema history found - writing the current schema as the initial baseline.");
                return 0;
            }
            Console.WriteLine("No breaking changes detected - nothing to create.");
            return 0;
        }

        foreach (var orphan in newlyOrphaned)
            Console.WriteLine($"  Table '{orphan.DatabaseFullName}.{orphan.Accessor}' is no longer declared - marked removed at generation {orphan.RemovedAtGeneration}.");

        var migrationsDirectory = Path.Combine(projectDirectory, config.MigrationsOutputDirectory);
        Directory.CreateDirectory(migrationsDirectory);

        foreach (var (rowTypeFullName, pairs) in breakingByRowType) {
            var fromRevision = oldDescriptor.TypeRevisions.GetValueOrDefault(rowTypeFullName, 0);
            var toRevision = fromRevision + 1;
            var representative = pairs[0].Old;

            WriteFrozenSnapshot(migrationsDirectory, rowTypeFullName, fromRevision, representative);
            WriteMigrationStub(migrationsDirectory, rowTypeFullName, fromRevision, toRevision);

            newDescriptor.TypeRevisions[rowTypeFullName] = toRevision;

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
