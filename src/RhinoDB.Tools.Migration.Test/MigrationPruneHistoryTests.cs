using RhinoDB.SchemaContracts;

namespace RhinoDB.Tools.Migration.Test;

// `rhinodb migration prune-history` - a pure file-system/descriptor operation, no compilation needed (unlike
// `create`/`status`), so the fixture here is minimal: an empty .csproj just for ProjectPathArg.Resolve to
// find, a hand-built Descriptor.json, and a Migrations/ folder with files matching the naming convention
// MigrationCreateCommand's own WriteFrozenSnapshot/WriteMigrationStub already produce.
public class MigrationPruneHistoryTests {
    static string CreateProjectDirectory() {
        var projectDirectory = Path.Combine(Path.GetTempPath(), "RhinoDBPruneHistoryTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(projectDirectory);
        File.WriteAllText(Path.Combine(projectDirectory, "Sample.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        return projectDirectory;
    }

    static void WriteDescriptor(string projectDirectory, DatabaseContractDescriptor descriptor) {
        var descriptorDirectory = Path.Combine(projectDirectory, "RhinoContracts");
        Directory.CreateDirectory(descriptorDirectory);
        File.WriteAllText(Path.Combine(descriptorDirectory, "Descriptor.json"), ContractDescriptorJson.Serialize(descriptor));
    }

    static string WriteMigrationFile(string projectDirectory, string fileName, string content = "// stub") {
        var migrationsDirectory = Path.Combine(projectDirectory, "Migrations");
        Directory.CreateDirectory(migrationsDirectory);
        var path = Path.Combine(migrationsDirectory, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    static DatabaseContractDescriptor DescriptorWithOneTable(int retainedFromGeneration, List<RevisionHistoryEntry> history) =>
        new DatabaseContractDescriptor {
            Databases = [new DatabaseGenerationState { FullName = "global::Test.Db", Generation = history.Count, RetainedFromGeneration = retainedFromGeneration }],
            Tables = [
                new TableDescriptor {
                    DatabaseFullName = "global::Test.Db",
                    Accessor = "Widget",
                    RowTypeFullName = "global::Test.Widget",
                    Kind = "Persistent",
                    RevisionHistory = history,
                },
            ],
        };

    [Test]
    public void PruneHistory_ARevisionBelowTheRetentionFloor_DeletesTheSnapshotAndFlagsTheStubButNeverDeletesIt() {
        var projectDirectory = CreateProjectDirectory();
        try {
            WriteDescriptor(projectDirectory, DescriptorWithOneTable(
                retainedFromGeneration: 2,
                history: [new RevisionHistoryEntry { Generation = 1, Revision = 1 }, new RevisionHistoryEntry { Generation = 2, Revision = 2 }]));

            var rev0Snapshot = WriteMigrationFile(projectDirectory, "Widget_Rev0.g.cs");
            var rev0Stub = WriteMigrationFile(projectDirectory, "Widget_FromRev0.cs");
            var rev1Snapshot = WriteMigrationFile(projectDirectory, "Widget_Rev1.g.cs");
            var rev1Stub = WriteMigrationFile(projectDirectory, "Widget_FromRev1.cs");
            var rev2Snapshot = WriteMigrationFile(projectDirectory, "Widget_Rev2.g.cs");

            var originalOut = Console.Out;
            var capturedOut = new StringWriter();
            Console.SetOut(capturedOut);
            var exitCode = MigrationTool.Run(["prune-history", "--project", Path.Combine(projectDirectory, "Sample.csproj")]);
            Console.SetOut(originalOut);

            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(File.Exists(rev0Snapshot), Is.False, "revision 0 is below the floor (2) - the dead frozen snapshot must be deleted.");
            Assert.That(File.Exists(rev1Snapshot), Is.False, "revision 1 is also below the floor (2) - dead too.");
            Assert.That(File.Exists(rev2Snapshot), Is.True, "revision 2 IS the floor - still needed, must survive.");
            Assert.That(File.Exists(rev0Stub), Is.True, "the migration stub is developer-authored logic - never auto-deleted.");
            Assert.That(File.Exists(rev1Stub), Is.True);
            Assert.That(capturedOut.ToString(), Does.Contain("Widget_FromRev0.cs").And.Contain("safe to remove by hand"));
            Assert.That(capturedOut.ToString(), Does.Contain("Widget_FromRev1.cs"));
        } finally {
            Directory.Delete(projectDirectory, recursive: true);
        }
    }

    [Test]
    public void PruneHistory_EverythingStillNeeded_PrintsNothingToPruneAndDeletesNothing() {
        var projectDirectory = CreateProjectDirectory();
        try {
            WriteDescriptor(projectDirectory, DescriptorWithOneTable(retainedFromGeneration: 0, history: []));
            var snapshot = WriteMigrationFile(projectDirectory, "Widget_Rev0.g.cs");

            var originalOut = Console.Out;
            var capturedOut = new StringWriter();
            Console.SetOut(capturedOut);
            var exitCode = MigrationTool.Run(["prune-history", "--project", Path.Combine(projectDirectory, "Sample.csproj")]);
            Console.SetOut(originalOut);

            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(File.Exists(snapshot), Is.True);
            Assert.That(capturedOut.ToString(), Does.Contain("Nothing to prune"));
        } finally {
            Directory.Delete(projectDirectory, recursive: true);
        }
    }

    [Test]
    public void PruneHistory_NoDescriptorAtAll_PrintsNothingToPruneWithoutThrowing() {
        var projectDirectory = CreateProjectDirectory();
        try {
            var originalOut = Console.Out;
            var capturedOut = new StringWriter();
            Console.SetOut(capturedOut);
            var exitCode = MigrationTool.Run(["prune-history", "--project", Path.Combine(projectDirectory, "Sample.csproj")]);
            Console.SetOut(originalOut);

            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(capturedOut.ToString(), Does.Contain("No committed Descriptor.json"));
        } finally {
            Directory.Delete(projectDirectory, recursive: true);
        }
    }

    [Test]
    public void PruneHistory_ARowTypeSharedByTwoTablesWithDifferentFloors_UsesTheMostConservativeOne() {
        var projectDirectory = CreateProjectDirectory();
        try {
            var descriptor = new DatabaseContractDescriptor {
                Databases = [
                    new DatabaseGenerationState { FullName = "global::Test.DbA", Generation = 2, RetainedFromGeneration = 2 },
                    new DatabaseGenerationState { FullName = "global::Test.DbB", Generation = 1, RetainedFromGeneration = 1 },
                ],
                Tables = [
                    new TableDescriptor {
                        DatabaseFullName = "global::Test.DbA", Accessor = "WidgetA", RowTypeFullName = "global::Test.Widget", Kind = "Persistent",
                        RevisionHistory = [new RevisionHistoryEntry { Generation = 1, Revision = 1 }, new RevisionHistoryEntry { Generation = 2, Revision = 2 }],
                    },
                    new TableDescriptor {
                        DatabaseFullName = "global::Test.DbB", Accessor = "WidgetB", RowTypeFullName = "global::Test.Widget", Kind = "Persistent",
                        RevisionHistory = [new RevisionHistoryEntry { Generation = 1, Revision = 1 }],
                    },
                ],
            };
            WriteDescriptor(projectDirectory, descriptor);

            var rev0Snapshot = WriteMigrationFile(projectDirectory, "Widget_Rev0.g.cs");
            var rev1Snapshot = WriteMigrationFile(projectDirectory, "Widget_Rev1.g.cs");

            var exitCode = MigrationTool.Run(["prune-history", "--project", Path.Combine(projectDirectory, "Sample.csproj")]);

            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(File.Exists(rev0Snapshot), Is.False, "revision 0 is below BOTH tables' floors - safe to prune.");
            Assert.That(File.Exists(rev1Snapshot), Is.True,
                "DbA's own floor (revision 2) would allow pruning revision 1, but DbB's floor is only revision 1 - " +
                "the shared row type is only as prunable as its most conservative referencing table.");
        } finally {
            Directory.Delete(projectDirectory, recursive: true);
        }
    }
}
