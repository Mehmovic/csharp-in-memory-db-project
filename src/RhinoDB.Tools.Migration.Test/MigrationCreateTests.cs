using System.Runtime.CompilerServices;

namespace RhinoDB.Tools.Migration.Test;

// `rhinodb migration create` end-to-end. The fixture's committed Descriptor.json (checked into git,
// never mutated in place) describes Widget.Name as `int`; the live fixture source declares it `string` -
// a genuine Breaking retype. Each test copies the fixture to a fresh temp directory (rewriting the
// ProjectReference to an absolute path, since the temp copy sits at a different relative depth from
// RhinoDB.Core) so `migration create`'s real file writes/Descriptor.json rewrite never touch the
// git-tracked fixture itself.
public class MigrationCreateTests {
    static string FixtureSourceDirectory([CallerFilePath] string here = "") =>
        Path.Combine(Path.GetDirectoryName(here)!, "Fixtures", "CreateSampleProject");

    static string RhinoDbCoreCsprojPath([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "RhinoDB.Core", "RhinoDB.Core.csproj"));

    static string CopyFixtureToTempDirectory() {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "RhinoDBMigrationCreateTest_" + Guid.NewGuid().ToString("N"));
        CopyDirectory(FixtureSourceDirectory(), tempDirectory);

        var csprojPath = Path.Combine(tempDirectory, "CreateSampleProject.csproj");
        var rewritten = File.ReadAllText(csprojPath).Replace(
            @"..\..\..\RhinoDB.Core\RhinoDB.Core.csproj", RhinoDbCoreCsprojPath());
        File.WriteAllText(csprojPath, rewritten);

        return csprojPath;
    }

    static void CopyDirectory(string sourceDir, string destDir) {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)));
        foreach (var subDir in Directory.GetDirectories(sourceDir))
            CopyDirectory(subDir, Path.Combine(destDir, Path.GetFileName(subDir)));
    }

    [Test]
    public void MigrationCreate_AgainstABreakingChange_WritesFrozenSnapshotAndStubAndUpdatesDescriptor() {
        var csprojPath = CopyFixtureToTempDirectory();
        var projectDirectory = Path.GetDirectoryName(csprojPath)!;
        try {
            var exitCode = RhinoDB.Tools.Migration.MigrationTool.Run(["create", "--project", csprojPath]);
            Assert.That(exitCode, Is.EqualTo(0));

            var frozenPath = Path.Combine(projectDirectory, "Migrations", "Widget_Rev0.g.cs");
            var stubPath = Path.Combine(projectDirectory, "Migrations", "Widget_FromRev0.cs");
            Assert.That(File.Exists(frozenPath), Is.True, "frozen snapshot file should exist");
            Assert.That(File.Exists(stubPath), Is.True, "migration stub file should exist");

            var frozenContent = File.ReadAllText(frozenPath);
            Assert.That(frozenContent, Does.Contain("namespace CreateSampleProject.SchemaHistory.Revision0;"));
            Assert.That(frozenContent, Does.Contain("[FrozenSchema(0)]"));
            Assert.That(frozenContent, Does.Contain("[PrimaryKey] int Id"));
            Assert.That(frozenContent, Does.Contain("int Name"));

            var stubContent = File.ReadAllText(stubPath);
            Assert.That(stubContent, Does.Contain("namespace CreateSampleProject;"));
            Assert.That(stubContent, Does.Contain("[RhinoDB.Core.Tables.Migration(0)]"));
            Assert.That(stubContent, Does.Contain("FromRevision0(CreateSampleProject.SchemaHistory.Revision0.Widget_Rev0 old)"));

            var descriptorPath = Path.Combine(projectDirectory, "RhinoContracts", "Descriptor.json");
            var descriptor = RhinoDB.SchemaContracts.ContractDescriptorJson.Parse(File.ReadAllText(descriptorPath));
            Assert.That(descriptor.TypeRevisions["global::CreateSampleProject.Widget"], Is.EqualTo(1));
            Assert.That(descriptor.Tables.Single().Revision, Is.EqualTo(1));
            Assert.That(descriptor.Databases.Single(d => d.FullName == "global::CreateSampleProject.SampleDb").Generation, Is.EqualTo(1));

            var history = descriptor.Tables.Single().RevisionHistory;
            Assert.That(history, Has.Count.EqualTo(1), "the fixture's committed descriptor had no prior history - this run's hop is the only entry");
            Assert.That(history[0].Generation, Is.EqualTo(1));
            Assert.That(history[0].Revision, Is.EqualTo(1));
        } finally {
            Directory.Delete(projectDirectory, recursive: true);
        }
    }

    [Test]
    public void MigrationCreate_RunTwiceInARowWithoutReverting_TheSecondRunFindsNothingLeftToCreate() {
        // After the first run, Descriptor.json already reflects the current live shape - a second run has
        // no Breaking change left to detect (the real, common case: create, commit, move on).
        var csprojPath = CopyFixtureToTempDirectory();
        var projectDirectory = Path.GetDirectoryName(csprojPath)!;
        var originalOut = Console.Out;
        try {
            Assert.That(RhinoDB.Tools.Migration.MigrationTool.Run(["create", "--project", csprojPath]), Is.EqualTo(0));

            var capturedOut = new StringWriter();
            Console.SetOut(capturedOut);
            var secondExitCode = RhinoDB.Tools.Migration.MigrationTool.Run(["create", "--project", csprojPath]);
            Console.SetOut(originalOut);

            Assert.That(secondExitCode, Is.EqualTo(0));
            Assert.That(capturedOut.ToString(), Does.Contain("No breaking changes detected"));
        } finally {
            Console.SetOut(originalOut);
            Directory.Delete(projectDirectory, recursive: true);
        }
    }

    [Test]
    public void MigrationCreate_TwoSuccessiveBreakingChanges_AccumulatesRevisionHistoryInsteadOfOverwritingIt() {
        var csprojPath = CopyFixtureToTempDirectory();
        var projectDirectory = Path.GetDirectoryName(csprojPath)!;
        try {
            Assert.That(RhinoDB.Tools.Migration.MigrationTool.Run(["create", "--project", csprojPath]), Is.EqualTo(0));

            // A second, independent breaking change: Name goes from string to decimal.
            var widgetPath = Path.Combine(projectDirectory, "Widget.cs");
            File.WriteAllText(widgetPath, File.ReadAllText(widgetPath).Replace("string Name", "decimal Name"));

            Assert.That(RhinoDB.Tools.Migration.MigrationTool.Run(["create", "--project", csprojPath]), Is.EqualTo(0));

            var descriptorPath = Path.Combine(projectDirectory, "RhinoContracts", "Descriptor.json");
            var descriptor = RhinoDB.SchemaContracts.ContractDescriptorJson.Parse(File.ReadAllText(descriptorPath));
            var table = descriptor.Tables.Single();

            Assert.That(table.Revision, Is.EqualTo(2));
            Assert.That(table.RevisionHistory.Select(h => (h.Generation, h.Revision)), Is.EqualTo(new[] { (1, 1), (2, 2) }),
                "the second run's hop must be APPENDED, not replace the first run's - the whole point of RevisionHistory " +
                "is answering 'what revision was this table's data at generation N' for every N it's ever passed through.");

            Assert.That(File.Exists(Path.Combine(projectDirectory, "Migrations", "Widget_Rev1.g.cs")), Is.True);
            Assert.That(File.Exists(Path.Combine(projectDirectory, "Migrations", "Widget_FromRev1.cs")), Is.True);
        } finally {
            Directory.Delete(projectDirectory, recursive: true);
        }
    }
}
