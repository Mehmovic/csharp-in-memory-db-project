namespace RhinoDB.SchemaContracts.Test;

// The walk-up is what makes `rhinodb <tool>` work from anywhere inside a project, which is how a
// person actually invokes it. The start directory is a parameter so none of this needs to touch
// the process working directory.
public class ProjectPathArgTests {
    private string root = "";

    [SetUp]
    public void SetUp() {
        root = Path.Combine(Path.GetTempPath(), "rhinodb-projectpath-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
    }

    [TearDown]
    public void TearDown() {
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    static private void MakeProject(string directory, string projectName = "Game.csproj") {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, projectName), "<Project />");
        File.WriteAllText(Path.Combine(directory, GeneratorConfigLoader.ConfigFileName), "{}");
    }

    [Test]
    public void Resolve_StartedInsideTheProjectRoot_FindsThatProject() {
        MakeProject(root);

        var resolved = ProjectPathArg.Resolve([], out var error, root);

        Assert.That(error, Is.Null);
        Assert.That(Path.GetFileName(resolved!), Is.EqualTo("Game.csproj"));
    }

    [Test]
    public void Resolve_StartedInANestedSubdirectory_WalksUpToTheProjectRoot() {
        MakeProject(root);
        var nested = Path.Combine(root, "Server", "Handlers");
        Directory.CreateDirectory(nested);

        var resolved = ProjectPathArg.Resolve([], out var error, nested);

        Assert.That(error, Is.Null, "a subdirectory is where a person usually stands");
        Assert.That(Path.GetFullPath(resolved!), Is.EqualTo(Path.GetFullPath(Path.Combine(root, "Game.csproj"))));
    }

    [Test]
    public void Resolve_WithAnInnerProject_UsesTheNearestOneNotAnOuterOne() {
        MakeProject(root, "Outer.csproj");
        var inner = Path.Combine(root, "Inner");
        MakeProject(inner, "Inner.csproj");

        var resolved = ProjectPathArg.Resolve([], out var error, inner);

        Assert.That(error, Is.Null);
        Assert.That(Path.GetFileName(resolved!), Is.EqualTo("Inner.csproj"), "the nearest project root wins");
    }

    [Test]
    public void Resolve_WithAnExplicitProject_IgnoresTheSettingsFileEntirely() {
        MakeProject(root);
        var elsewhere = Path.Combine(root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        var explicitProject = Path.Combine(elsewhere, "Other.csproj");
        File.WriteAllText(explicitProject, "<Project />");

        var resolved = ProjectPathArg.Resolve(["--project", explicitProject], out var error, root);

        Assert.That(error, Is.Null);
        Assert.That(Path.GetFullPath(resolved!), Is.EqualTo(Path.GetFullPath(explicitProject)));
    }

    [Test]
    public void Resolve_WithAnExplicitProjectThatDoesNotExist_FailsNamingTheFlag() {
        MakeProject(root);

        var resolved = ProjectPathArg.Resolve(["--project", Path.Combine(root, "Nope.csproj")], out var error, root);

        Assert.That(resolved, Is.Null);
        Assert.That(error, Does.Contain("--project"));
    }

    [Test]
    public void Resolve_WithNoSettingsFileAnywhereUpTheTree_FallsBackToTheStartDirectory() {
        File.WriteAllText(Path.Combine(root, "Loose.csproj"), "<Project />");

        var resolved = ProjectPathArg.Resolve([], out var error, root);

        Assert.That(error, Is.Null, "a plain directory with one csproj is still usable");
        Assert.That(Path.GetFileName(resolved!), Is.EqualTo("Loose.csproj"));
    }

    [Test]
    public void Resolve_WithNothingToGoOn_FailsAndSaysHowToFixIt() {
        var nested = Path.Combine(root, "empty");
        Directory.CreateDirectory(nested);

        var resolved = ProjectPathArg.Resolve([], out var error, nested);

        Assert.That(resolved, Is.Null);
        Assert.That(error, Does.Contain("--project"));
    }

    [Test]
    public void Resolve_AtAProjectRootWithTwoProjects_FailsRatherThanGuessing() {
        MakeProject(root, "Server.csproj");
        File.WriteAllText(Path.Combine(root, "Client.csproj"), "<Project />");

        var resolved = ProjectPathArg.Resolve([], out var error, root);

        Assert.That(resolved, Is.Null);
        Assert.That(error, Does.Contain("Server.csproj").And.Contain("Client.csproj"));
    }
}
