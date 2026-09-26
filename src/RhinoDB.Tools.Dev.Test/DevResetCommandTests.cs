namespace RhinoDB.Tools.Dev.Test;

// `rhinodb dev reset` never needs a real compilation/MSBuildWorkspace - it only ever touches
// Descriptor.json/Migrations/a cold-storage directory by plain file I/O, so these tests build a fresh
// temp directory by hand rather than reusing RhinoDB.Tools.Migration.Test's Fixtures/ pattern.
public class DevResetCommandTests {
    static string CreateTempProjectDirectory() {
        var dir = Path.Combine(Path.GetTempPath(), "RhinoDBDevResetTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Sample.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        return dir;
    }

    static string CaptureOut(Func<int> run, out int exitCode) {
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
    public void DevReset_DryRunByDefault_ListsTargetsWithoutDeletingAnything() {
        var projectDirectory = CreateTempProjectDirectory();
        try {
            var descriptorPath = Path.Combine(projectDirectory, "RhinoContracts", "Descriptor.json");
            Directory.CreateDirectory(Path.GetDirectoryName(descriptorPath)!);
            File.WriteAllText(descriptorPath, "{}");

            var migrationsDir = Path.Combine(projectDirectory, "Migrations");
            Directory.CreateDirectory(migrationsDir);
            File.WriteAllText(Path.Combine(migrationsDir, "Widget_Rev0.g.cs"), "// frozen");

            var coldPath = Path.Combine(projectDirectory, "data");
            Directory.CreateDirectory(coldPath);
            File.WriteAllText(Path.Combine(coldPath, "wal.dat"), "fake wal bytes");

            var output = CaptureOut(() => DevTool.Run([
                "reset", "--project", Path.Combine(projectDirectory, "Sample.csproj"), "--cold-path", coldPath,
            ]), out var exitCode);

            Assert.That(exitCode, Is.EqualTo(1), "a dry run must not report success - the developer has to pass --yes.");
            Assert.That(output, Does.Contain("Would delete"));
            Assert.That(File.Exists(descriptorPath), Is.True, "dry run must not touch Descriptor.json");
            Assert.That(Directory.Exists(migrationsDir), Is.True, "dry run must not touch Migrations/");
            Assert.That(Directory.Exists(coldPath), Is.True, "dry run must not touch the cold-storage directory");
        } finally {
            Directory.Delete(projectDirectory, recursive: true);
        }
    }

    [Test]
    public void DevReset_WithYes_DeletesDescriptorMigrationsAndColdPathDirectory() {
        var projectDirectory = CreateTempProjectDirectory();
        try {
            var descriptorPath = Path.Combine(projectDirectory, "RhinoContracts", "Descriptor.json");
            Directory.CreateDirectory(Path.GetDirectoryName(descriptorPath)!);
            File.WriteAllText(descriptorPath, "{}");

            var migrationsDir = Path.Combine(projectDirectory, "Migrations");
            Directory.CreateDirectory(migrationsDir);
            File.WriteAllText(Path.Combine(migrationsDir, "Widget_Rev0.g.cs"), "// frozen");

            var coldPathA = Path.Combine(projectDirectory, "dataA");
            var coldPathB = Path.Combine(projectDirectory, "dataB");
            Directory.CreateDirectory(coldPathA);
            Directory.CreateDirectory(coldPathB);
            File.WriteAllText(Path.Combine(coldPathA, "wal.dat"), "fake wal bytes");

            var exitCode = DevTool.Run([
                "reset", "--project", Path.Combine(projectDirectory, "Sample.csproj"),
                "--cold-path", coldPathA, "--cold-path", coldPathB, "--yes",
            ]);

            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(File.Exists(descriptorPath), Is.False);
            Assert.That(Directory.Exists(migrationsDir), Is.False);
            Assert.That(Directory.Exists(coldPathA), Is.False);
            Assert.That(Directory.Exists(coldPathB), Is.False);
        } finally {
            Directory.Delete(projectDirectory, recursive: true);
        }
    }

    [Test]
    public void DevReset_NothingPresent_ReturnsZeroAndReportsNothingToDo() {
        var projectDirectory = CreateTempProjectDirectory();
        try {
            var output = CaptureOut(() => DevTool.Run([
                "reset", "--project", Path.Combine(projectDirectory, "Sample.csproj"), "--yes",
            ]), out var exitCode);

            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(output, Does.Contain("Nothing to reset"));
        } finally {
            Directory.Delete(projectDirectory, recursive: true);
        }
    }

    [Test]
    public void DevReset_ColdPathThatDoesNotExist_IsSkippedWithoutError() {
        var projectDirectory = CreateTempProjectDirectory();
        try {
            var missingColdPath = Path.Combine(projectDirectory, "does-not-exist");

            var exitCode = DevTool.Run([
                "reset", "--project", Path.Combine(projectDirectory, "Sample.csproj"), "--cold-path", missingColdPath, "--yes",
            ]);

            Assert.That(exitCode, Is.EqualTo(0));
        } finally {
            Directory.Delete(projectDirectory, recursive: true);
        }
    }
}
