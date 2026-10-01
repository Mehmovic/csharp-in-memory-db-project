using Microsoft.Build.Framework;

namespace RhinoDB.PreBuild.Test;

// ExpandShorthandTask.Execute is the MSBuild entry point the PreBuild props invokes. Nothing
// called it before, so the whole task - including the failure path that surfaces a config
// problem as a build error rather than a silent no-op - was unproven.
public class ExpandShorthandTaskExecuteTests {
    // Minimal IBuildEngine: Task.Log throws unless BuildEngine is set, and Microsoft.Build's
    // own test engine is not referenced by this project. Only the members Task actually
    // touches need to do anything.
    sealed class RecordingEngine : IBuildEngine {
        public List<string> Errors { get; } = [];
        public bool ContinueOnError => false;
        public int LineNumberOfTaskNode => 0;
        public int ColumnNumberOfTaskNode => 0;
        public string ProjectFileOfTaskNode => "test.csproj";

        public void LogErrorEvent(BuildErrorEventArgs e) => Errors.Add(e.Message ?? "");
        public void LogWarningEvent(BuildWarningEventArgs e) { }
        public void LogMessageEvent(BuildMessageEventArgs e) { }
        public void LogCustomEvent(CustomBuildEventArgs e) { }
        public bool BuildProjectFile(string projectFileName, string[] targetNames, System.Collections.IDictionary globalProperties, System.Collections.IDictionary targetOutputs) => true;
    }

    static private string CreateProjectDirectory() =>
        Path.Combine(Path.GetTempPath(), "RhinoDBExpandShorthandTask_" + Guid.NewGuid().ToString("N"));

    static private (bool Result, RecordingEngine Engine) Execute(string projectDirectory) {
        var engine = new RecordingEngine();
        var task = new ExpandShorthandTask { BuildEngine = engine, ProjectDirectory = projectDirectory };
        return (task.Execute(), engine);
    }

    [Test]
    public void Execute_WithAValidProjectDirectory_Succeeds() {
        var dir = CreateProjectDirectory();
        Directory.CreateDirectory(dir);
        try {
            File.WriteAllText(Path.Combine(dir, "rdbsettings.json"), """{ "Generator": { "SourceParentDirectory": "RhinoContracts", "TargetParentDirectory": "RhinoDB" } }""");
            Directory.CreateDirectory(Path.Combine(dir, "RhinoContracts", "Tables"));

            var (result, engine) = Execute(dir);

            Assert.Multiple(() => {
                Assert.That(result, Is.True);
                Assert.That(engine.Errors, Is.Empty);
            });
        } finally {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Test]
    public void Execute_WithNoSettingsFile_FailsAndLogsTheReason() {
        // A malformed rdbsettings.json makes GeneratorConfigLoader throw, which the runner
        // turns into an error. The task must surface it and return false - silently succeeding
        // would leave a project with no generated code and no explanation.
        // (A *missing* settings file is not a failure: the loader writes a default one.)
        var dir = CreateProjectDirectory();
        Directory.CreateDirectory(dir);
        try {
            File.WriteAllText(Path.Combine(dir, "rdbsettings.json"), "{ this is not json");

            var (result, engine) = Execute(dir);

            Assert.Multiple(() => {
                Assert.That(result, Is.False);
                Assert.That(engine.Errors, Is.Not.Empty, "the failure reason must reach the build log.");
            });
        } finally {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Test]
    public void Execute_AlwaysExpandsIntoTheTargetDirectory() {
        var dir = CreateProjectDirectory();
        Directory.CreateDirectory(dir);
        try {
            File.WriteAllText(Path.Combine(dir, "rdbsettings.json"), """{ "Generator": { "SourceParentDirectory": "RhinoContracts", "TargetParentDirectory": "RhinoDB" } }""");
            var tables = Path.Combine(dir, "RhinoContracts", "Tables");
            Directory.CreateDirectory(tables);
            File.WriteAllText(Path.Combine(tables, "Player.cs"), """
                using RhinoDB.PreBuild.Shorthand;

                namespace Sample;

                [Database]
                public partial class SampleDb { }

                [InstantTable(typeof(SampleDb))]
                public readonly partial record struct Player([PrimaryKey] int Id, string Name);
                """);

            var (result, _) = Execute(dir);

            Assert.That(result, Is.True);
            Assert.That(File.Exists(Path.Combine(dir, "RhinoDB", "Tables", "Player.g.cs")), Is.True,
                "the expanded output must land under the configured target directory.");
        } finally {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}