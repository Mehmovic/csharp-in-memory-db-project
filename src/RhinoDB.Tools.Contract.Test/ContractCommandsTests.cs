namespace RhinoDB.Tools.Contract.Test;

// "rhinodb contract generate/clean" - the protobuf-style manual counterpart to ExpandShorthandTask's
// automatic MSBuild invocation. Neither command needs MSBuildWorkspace/a real compilation - both are plain
// file I/O wrapping RhinoDB.PreBuild.ShorthandExpansionRunner, so a hand-built temp directory is enough.
public class ContractCommandsTests {
    private const string WidgetShorthand = """
        using RhinoDB.Core.Tables;

        namespace TestNs;

        [InstantTable(typeof(TestDb))]
        public readonly partial record struct Widget(
            [PrimaryKey] int Id,
            int Value
        );
        """;

    static private string CreateTempProjectDirectory() {
        var dir = Path.Combine(Path.GetTempPath(), "RhinoDBContractTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Sample.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        return dir;
    }

    static private string CaptureOut(Func<int> run, out int exitCode) {
        var originalOut = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        try {
            exitCode = run();
        } finally {
            Console.SetOut(originalOut);
        }
        return captured.ToString();
    }

    [Test]
    public void Generate_OneShorthandFile_WritesExpandedOutputAndReportsIt() {
        var projectDirectory = CreateTempProjectDirectory();
        try {
            var sourceDir = Path.Combine(projectDirectory, "RhinoContracts", "Tables");
            Directory.CreateDirectory(sourceDir);
            File.WriteAllText(Path.Combine(sourceDir, "Widget.cs"), WidgetShorthand);

            var output = CaptureOut(() => ContractTool.Run([
                "generate", "--project", Path.Combine(projectDirectory, "Sample.csproj"),
            ]), out var exitCode);

            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(output, Does.Contain("wrote"));
            Assert.That(File.Exists(Path.Combine(projectDirectory, "RhinoDB", "Tables", "Widget.g.cs")), Is.True);
        } finally {
            Directory.Delete(projectDirectory, recursive: true);
        }
    }

    [Test]
    public void Generate_DefaultCleansFirst_SecondCallStillReportsAWriteSinceItsOwnOutputWasJustDeleted() {
        var projectDirectory = CreateTempProjectDirectory();
        try {
            var sourceDir = Path.Combine(projectDirectory, "RhinoContracts", "Tables");
            Directory.CreateDirectory(sourceDir);
            File.WriteAllText(Path.Combine(sourceDir, "Widget.cs"), WidgetShorthand);

            ContractTool.Run(["generate", "--project", Path.Combine(projectDirectory, "Sample.csproj")]);

            var output = CaptureOut(() => ContractTool.Run([
                "generate", "--project", Path.Combine(projectDirectory, "Sample.csproj"),
            ]), out var exitCode);

            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(output, Does.Contain("wrote"), "cleaning first means the output was just deleted, so generate must re-write it");
        } finally {
            Directory.Delete(projectDirectory, recursive: true);
        }
    }

    [Test]
    public void Generate_NoCleanFlag_CalledTwiceWithNoSourceChanges_SecondCallReportsNothingToDo() {
        var projectDirectory = CreateTempProjectDirectory();
        try {
            var sourceDir = Path.Combine(projectDirectory, "RhinoContracts", "Tables");
            Directory.CreateDirectory(sourceDir);
            File.WriteAllText(Path.Combine(sourceDir, "Widget.cs"), WidgetShorthand);

            ContractTool.Run(["generate", "--project", Path.Combine(projectDirectory, "Sample.csproj"), "--no-clean"]);

            var output = CaptureOut(() => ContractTool.Run([
                "generate", "--project", Path.Combine(projectDirectory, "Sample.csproj"), "--no-clean",
            ]), out var exitCode);

            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(output, Does.Not.Contain("deleted"));
            Assert.That(output, Does.Contain("Nothing to do"));
        } finally {
            Directory.Delete(projectDirectory, recursive: true);
        }
    }

    [Test]
    public void Generate_NothingToDo_ReportsNothingToDo() {
        var projectDirectory = CreateTempProjectDirectory();
        try {
            var output = CaptureOut(() => ContractTool.Run([
                "generate", "--project", Path.Combine(projectDirectory, "Sample.csproj"),
            ]), out var exitCode);

            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(output, Does.Contain("Nothing to do"));
        } finally {
            Directory.Delete(projectDirectory, recursive: true);
        }
    }

    [Test]
    public void Clean_AfterGenerate_DeletesGeneratedFilesAndReportsThem() {
        var projectDirectory = CreateTempProjectDirectory();
        try {
            var sourceDir = Path.Combine(projectDirectory, "RhinoContracts", "Tables");
            Directory.CreateDirectory(sourceDir);
            File.WriteAllText(Path.Combine(sourceDir, "Widget.cs"), WidgetShorthand);
            ContractTool.Run(["generate", "--project", Path.Combine(projectDirectory, "Sample.csproj")]);

            var outputPath = Path.Combine(projectDirectory, "RhinoDB", "Tables", "Widget.g.cs");
            Assert.That(File.Exists(outputPath), Is.True, "precondition");

            var output = CaptureOut(() => ContractTool.Run([
                "clean", "--project", Path.Combine(projectDirectory, "Sample.csproj"),
            ]), out var exitCode);

            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(output, Does.Contain("deleted"));
            Assert.That(File.Exists(outputPath), Is.False);
        } finally {
            Directory.Delete(projectDirectory, recursive: true);
        }
    }

    [Test]
    public void Clean_NothingGenerated_ReportsNothingToClean() {
        var projectDirectory = CreateTempProjectDirectory();
        try {
            var output = CaptureOut(() => ContractTool.Run([
                "clean", "--project", Path.Combine(projectDirectory, "Sample.csproj"),
            ]), out var exitCode);

            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(output, Does.Contain("Nothing to clean"));
        } finally {
            Directory.Delete(projectDirectory, recursive: true);
        }
    }

    [Test]
    public void UnknownVerb_PrintsUsageAndReturnsNonZero() {
        var exitCode = ContractTool.Run(["bogus"]);

        Assert.That(exitCode, Is.EqualTo(1));
    }
}
