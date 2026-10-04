using RhinoDB.PreBuild;

namespace RhinoDB.PreBuild.Test;

// The disk-touching core behind both ExpandShorthandTask (MSBuild-invoked) and "rhinodb contract
// generate/clean" (manually invoked) - exercised directly here since it needs neither MSBuild nor a real
// build to run correctly.
public class ShorthandExpansionRunnerTests {
    private string tempDir = "";

    [SetUp]
    public void SetUp() {
        tempDir = Path.Combine(Path.GetTempPath(), "rhinodb-shorthand-expansion-runner-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
    }

    [TearDown]
    public void TearDown() {
        try { Directory.Delete(tempDir, recursive: true); } catch { }
    }

    private const string WidgetShorthand = """
        using RhinoDB.Core.Tables;

        namespace TestNs;

        [InstantTable<TestDb>]
        public readonly partial record struct Widget(
            [PrimaryKey] int Id,
            int Value
        );
        """;

    [Test]
    public void Generate_NoRhinoContractsDirectoryAtAll_SucceedsWithNothingWritten() {
        var outcome = ShorthandExpansionRunner.Generate(tempDir);

        Assert.That(outcome.Success, Is.True);
        Assert.That(outcome.WrittenFiles, Is.Empty);
        Assert.That(Directory.Exists(Path.Combine(tempDir, "RhinoContracts", "Tables")), Is.True);
        Assert.That(Directory.Exists(Path.Combine(tempDir, "RhinoContracts", "Types")), Is.True);
    }

    [Test]
    public void Generate_OneShorthandTableFile_WritesExpandedOutput() {
        var sourceDir = Path.Combine(tempDir, "RhinoContracts", "Tables");
        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(Path.Combine(sourceDir, "Widget.cs"), WidgetShorthand);

        var outcome = ShorthandExpansionRunner.Generate(tempDir);

        Assert.That(outcome.Success, Is.True);
        Assert.That(outcome.WrittenFiles, Has.Count.EqualTo(1));

        var outputPath = Path.Combine(tempDir, "RhinoDB", "Tables", "Widget.g.cs");
        Assert.That(File.Exists(outputPath), Is.True);
        Assert.That(File.ReadAllText(outputPath), Does.Contain("TableKind.Instant"));
    }

    [Test]
    public void Generate_CalledTwiceWithNoSourceChanges_SecondCallWritesNothingNew() {
        var sourceDir = Path.Combine(tempDir, "RhinoContracts", "Tables");
        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(Path.Combine(sourceDir, "Widget.cs"), WidgetShorthand);

        ShorthandExpansionRunner.Generate(tempDir);
        var second = ShorthandExpansionRunner.Generate(tempDir);

        Assert.That(second.Success, Is.True);
        Assert.That(second.WrittenFiles, Is.Empty, "unchanged shorthand source must not re-write identical output");
    }

    [Test]
    public void Generate_MalformedConfigJson_ReturnsErrorOutcomeInsteadOfThrowing() {
        File.WriteAllText(Path.Combine(tempDir, "rdbsettings.json"), "{ not valid json");

        var outcome = ShorthandExpansionRunner.Generate(tempDir);

        Assert.That(outcome.Success, Is.False);
        Assert.That(outcome.Errors, Is.Not.Empty);
    }

    [Test]
    public void Clean_AfterGenerate_DeletesGeneratedFilesAndTheNowEmptyTargetDirectory() {
        var sourceDir = Path.Combine(tempDir, "RhinoContracts", "Tables");
        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(Path.Combine(sourceDir, "Widget.cs"), WidgetShorthand);
        ShorthandExpansionRunner.Generate(tempDir);

        var outputPath = Path.Combine(tempDir, "RhinoDB", "Tables", "Widget.g.cs");
        Assert.That(File.Exists(outputPath), Is.True, "precondition: the file must exist before Clean");

        var deleted = ShorthandExpansionRunner.Clean(tempDir);

        Assert.That(deleted, Has.Count.EqualTo(1));
        Assert.That(File.Exists(outputPath), Is.False);
        Assert.That(Directory.Exists(Path.Combine(tempDir, "RhinoDB")), Is.False,
            "the now-empty target root should be removed too, not left behind");
    }

    [Test]
    public void Clean_NothingEverGenerated_ReturnsEmptyListWithoutThrowing() {
        var deleted = ShorthandExpansionRunner.Clean(tempDir);

        Assert.That(deleted, Is.Empty);
    }

    [Test]
    public void Generate_NestedSubdirectory_MirrorsTheSubdirectoryStructureInOutput() {
        var sourceDir = Path.Combine(tempDir, "RhinoContracts", "Tables", "Matches");
        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(Path.Combine(sourceDir, "Widget.cs"), WidgetShorthand);

        var outcome = ShorthandExpansionRunner.Generate(tempDir);

        Assert.That(outcome.Success, Is.True);
        Assert.That(File.Exists(Path.Combine(tempDir, "RhinoDB", "Tables", "Matches", "Widget.g.cs")), Is.True);
    }
}
