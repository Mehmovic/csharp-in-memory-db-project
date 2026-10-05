using System.Diagnostics;

namespace RhinoDB.Lib.Hosting.Test;

// The one test that lets the default policy actually kill a process: RhinoDB.Lib.Test.ExitFixture starts a real host
// (ExitProcess, the default), injects a WAL fsync failure on a Confirmed write and then waits 30 s. It must die long
// before that with the disk exit code and the one-line explanation on stderr - that's what a supervisor sees.
public class ProcessExitTests {
    private string dir = "";

    [SetUp]
    public void SetUp() => dir = Path.Combine(Path.GetTempPath(), "rhinodb-process-exit-tests", Guid.NewGuid().ToString("N"));

    [TearDown]
    public void TearDown() {
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    // src/RhinoDB.Lib.Test/bin/{Configuration}/{tfm}/ -> src/RhinoDB.Lib.Test.ExitFixture/bin/{Configuration}/{tfm}/
    static private string FixturePath() {
        var testBin = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var tfm = testBin.Name;
        var configuration = testBin.Parent!.Name;
        var src = testBin.Parent.Parent!.Parent!.Parent!.FullName;
        return Path.Combine(src, "RhinoDB.Lib.Test.ExitFixture", "bin", configuration, tfm, "RhinoDB.Lib.Test.ExitFixture.dll");
    }

    [Test]
    public async Task AWalFsyncFailure_UnderTheDefaultPolicy_ExitsTheProcessWithTheDiskCode() {
        var fixture = FixturePath();
        Assert.That(File.Exists(fixture), Is.True, $"precondition: the fixture was built at {fixture}.");

        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet") {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(fixture);
        start.ArgumentList.Add(dir);

        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try {
            await process.WaitForExitAsync(timeout.Token);
        } catch (OperationCanceledException) {
            process.Kill(entireProcessTree: true);
            Assert.Fail("the process kept running after the WAL failure - nothing made it exit.");
        }

        Assert.That(process.ExitCode, Is.EqualTo(UnrecoverableExitCodes.IoError), $"stderr: {await stderr}");
        Assert.That(await stderr, Does.Contain("unrecoverable error").And.Contain("Exiting with 74"));
    }
}
