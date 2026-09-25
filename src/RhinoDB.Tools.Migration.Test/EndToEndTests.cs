using System.Runtime.CompilerServices;

namespace RhinoDB.Tools.Migration.Test;

// The one real end-to-end case (per Phase 3, step 17): drives a genuine MSBuildWorkspace load against a
// small fixture .csproj checked into this project, through the actual MigrationTool.Run entry point - not
// CompilationWalker in isolation (already covered by CompilationWalkerTests.cs's in-memory compilations).
public class EndToEndTests {
    static string FixtureProjectPath([CallerFilePath] string here = "") =>
        Path.Combine(Path.GetDirectoryName(here)!, "Fixtures", "SampleProject", "SampleProject.csproj");

    [Test]
    public void MigrationStatus_AgainstARealMSBuildWorkspaceLoadedProject_ReportsTheSampleTableAsNew() {
        var originalOut = Console.Out;
        var capturedOut = new StringWriter();
        Console.SetOut(capturedOut);

        int exitCode;
        try {
            exitCode = RhinoDB.Tools.Migration.MigrationTool.Run(["status", "--project", FixtureProjectPath()]);
        } finally {
            Console.SetOut(originalOut);
        }

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(capturedOut.ToString(), Does.Contain("SampleDb.Widget: Unchanged (new)"));
    }
}
