using RhinoDB.PreBuild;

namespace RhinoDB.PreBuild.Test;

// Paths are built with Path.Combine under a rooted base (they never have to exist), so the same cases hold on
// Windows and Linux - a literal like C:\proj\... isn't even a rooted path on Linux.
public class ExpandShorthandTaskTests {
    static private readonly string Proj = Path.Combine(Path.GetTempPath(), "proj");
    static private readonly string SourceDir = Path.Combine(Proj, "RhinoContracts", "Tables");
    static private readonly string OutputDir = Path.Combine(Proj, "Rhino", "Tables");

    static private string Source(params string[] parts) => Path.Combine([SourceDir, .. parts]);
    static private string Output(params string[] parts) => Path.Combine([OutputDir, .. parts]);

    [Test]
    public void ComputeOutputPath_TopLevelFile_MirrorsIntoTheOutputRootDirectly() {
        var result = ShorthandExpansionRunner.ComputeOutputPath(SourceDir, OutputDir, Source("Widget.cs"));

        Assert.That(result, Is.EqualTo(Output("Widget.g.cs")));
    }

    [Test]
    public void ComputeOutputPath_NestedSubdirectory_MirrorsTheSubdirectoryStructure() {
        // RhinoContracts/Tables/Matches/PvPTable.cs -> Rhino/Tables/Matches/PvPTable.g.cs
        var result = ShorthandExpansionRunner.ComputeOutputPath(SourceDir, OutputDir, Source("Matches", "PvPTable.cs"));

        Assert.That(result, Is.EqualTo(Output("Matches", "PvPTable.g.cs")));
    }

    [Test]
    public void ComputeOutputPath_TwoFilesInTheSameNestedSubdirectory_EachMirrorsCorrectly() {
        var pvp = ShorthandExpansionRunner.ComputeOutputPath(SourceDir, OutputDir, Source("Matches", "PvPTable.cs"));
        var pve = ShorthandExpansionRunner.ComputeOutputPath(SourceDir, OutputDir, Source("Matches", "PvETable.cs"));

        Assert.That(pvp, Is.EqualTo(Output("Matches", "PvPTable.g.cs")));
        Assert.That(pve, Is.EqualTo(Output("Matches", "PvETable.g.cs")));
    }

    [Test]
    public void ComputeOutputPath_MultiLevelNesting_MirrorsAllLevels() {
        var result = ShorthandExpansionRunner.ComputeOutputPath(SourceDir, OutputDir, Source("Matches", "Ranked", "PvPTable.cs"));

        Assert.That(result, Is.EqualTo(Output("Matches", "Ranked", "PvPTable.g.cs")));
    }
}
