using System.Runtime.CompilerServices;

namespace RhinoDB.Lib.Server.Test;

// The permanent copy of Docs/04-networking.md's "agnosticism test" - zero Microsoft.AspNetCore/
// System.Net.Http references anywhere in RhinoDB.Lib.Server outside NetworkHost.cs, the one file
// Kestrel is allowed to appear in. Must stay green as Stage 8 adds real dispatch logic on top.
public class AgnosticismTests {
    static private string ProjectSourceDirectory([CallerFilePath] string here = "") =>
        Path.Combine(Path.GetDirectoryName(here)!, "..", "RhinoDB.Lib.Server");

    [Test]
    public void NoFileOtherThanNetworkHost_ReferencesAspNetCoreOrSystemNetHttp() {
        var projectDir = Path.GetFullPath(ProjectSourceDirectory());
        Assert.That(Directory.Exists(projectDir), Is.True, $"expected to find RhinoDB.Lib.Server at '{projectDir}'.");

        var offenders = Directory.EnumerateFiles(projectDir, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(path => Path.GetFileName(path) != "NetworkHost.cs")
            .Where(path => File.ReadAllLines(path).Any(line => {
                var trimmed = line.TrimStart();
                return trimmed.StartsWith("using Microsoft.AspNetCore") || trimmed.StartsWith("using System.Net.Http");
            }))
            .ToArray();

        Assert.That(offenders, Is.Empty,
            "only NetworkHost.cs may reference Microsoft.AspNetCore/System.Net.Http - offending files: "
            + string.Join(", ", offenders.Select(Path.GetFileName)));
    }
}
