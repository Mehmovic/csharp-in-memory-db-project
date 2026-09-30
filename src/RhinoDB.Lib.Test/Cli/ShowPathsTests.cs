using RhinoDB.Lib.Cli;

namespace RhinoDB.Lib.Test.Cli;

// --show-paths is a diagnostic: it must answer with the paths the real command would use, and do
// nothing else. These tests assert the printed text, and the exit code, by capturing the console.
public class ShowPathsTests {
    private string root = "";

    [SetUp]
    public void SetUp() {
        root = Path.Combine(Path.GetTempPath(), "rhinodb-showpaths-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
    }

    [TearDown]
    public void TearDown() {
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    static private void MakeProject(string directory, string projectName = "Game.csproj") {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, projectName), "<Project />");
        File.WriteAllText(Path.Combine(directory, "rdbsettings.json"), "{}");
    }

    // The flag is answered from the process working directory, so each test runs inside the
    // directory it cares about and restores the previous one afterwards.
    static private (int ExitCode, string Output) InDirectory(string directory, string[] args) {
        var previous = Directory.GetCurrentDirectory();
        var writer = new StringWriter();
        var previousOut = Console.Out;
        try {
            Directory.SetCurrentDirectory(directory);
            Console.SetOut(writer);
            return (ShowPaths.Run(args), writer.ToString());
        } finally {
            Console.SetOut(previousOut);
            Directory.SetCurrentDirectory(previous);
        }
    }

    [Test]
    public void ShowPaths_WithAnExplicitProject_PrintsThatProjectDirectory() {
        MakeProject(root);

        var (exitCode, output) = InDirectory(root, ["--show-paths", "--project", Path.Combine(root, "Game.csproj")]);

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Does.Contain(Path.GetFullPath(root)));
    }

    [Test]
    public void ShowPaths_PrintsTheProjectDirectoryNotTheProjectFile() {
        MakeProject(root);

        var (_, output) = InDirectory(root, ["--show-paths"]);

        Assert.That(output, Does.Contain("project").IgnoreCase);
        Assert.That(output, Does.Not.Contain("Game.csproj"), "callers act on the directory, so that is what is reported");
    }

    [Test]
    public void ShowPaths_WithNoColdPath_SaysSoRatherThanPrintingNothing() {
        MakeProject(root);

        var (_, output) = InDirectory(root, ["--show-paths"]);

        Assert.That(output, Does.Contain("cold"));
        Assert.That(output, Does.Contain("none").IgnoreCase);
    }

    [Test]
    public void ShowPaths_WithAColdPath_PrintsIt() {
        MakeProject(root);
        var cold = Path.Combine(root, "data");
        Directory.CreateDirectory(cold);

        var (exitCode, output) = InDirectory(root, ["--show-paths", "--cold-path", cold]);

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Does.Contain(Path.GetFullPath(cold)));
    }

    [Test]
    public void ShowPaths_WithSeveralColdPaths_PrintsEveryOne() {
        MakeProject(root);
        var first = Path.Combine(root, "data1");
        var second = Path.Combine(root, "data2");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);

        var (_, output) = InDirectory(root, ["--show-paths", "--cold-path", first, "--cold-path", second]);

        Assert.That(output, Does.Contain(Path.GetFullPath(first)));
        Assert.That(output, Does.Contain(Path.GetFullPath(second)));
    }

    [Test]
    public void ShowPaths_WithNothingResolvable_FailsRatherThanPrintingAPartialAnswer() {
        var empty = Path.Combine(root, "empty");
        Directory.CreateDirectory(empty);

        var (exitCode, _) = InDirectory(empty, ["--show-paths"]);

        Assert.That(exitCode, Is.EqualTo(1), "a wrong answer is worse than no answer");
    }

    [Test]
    public void Requested_IsTrueForTheFlagAnywhereInTheArguments() {
        Assert.That(ShowPaths.Requested(["contract", "generate", "--show-paths"]), Is.True);
        Assert.That(ShowPaths.Requested(["--show-paths"]), Is.True);
        Assert.That(ShowPaths.Requested(["contract", "generate"]), Is.False);
    }
}
