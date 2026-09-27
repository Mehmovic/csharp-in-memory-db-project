using RhinoDB.PreBuild;

namespace RhinoDB.PreBuild.Test;

public class ExpandShorthandTaskTests {
    [Test]
    public void ComputeOutputPath_TopLevelFile_MirrorsIntoTheOutputRootDirectly() {
        var sourceDir = @"C:\proj\RhinoContracts\Tables";
        var outputDir = @"C:\proj\Rhino\Tables";
        var sourceFile = @"C:\proj\RhinoContracts\Tables\Widget.cs";

        var result = ShorthandExpansionRunner.ComputeOutputPath(sourceDir, outputDir, sourceFile);

        Assert.That(result, Is.EqualTo(@"C:\proj\Rhino\Tables\Widget.g.cs"));
    }

    [Test]
    public void ComputeOutputPath_NestedSubdirectory_MirrorsTheSubdirectoryStructure() {
        // RhinoContracts/Tables/Matches/PvPTable.cs -> Rhino/Tables/Matches/PvPTable.g.cs
        var sourceDir = @"C:\proj\RhinoContracts\Tables";
        var outputDir = @"C:\proj\Rhino\Tables";
        var sourceFile = @"C:\proj\RhinoContracts\Tables\Matches\PvPTable.cs";

        var result = ShorthandExpansionRunner.ComputeOutputPath(sourceDir, outputDir, sourceFile);

        Assert.That(result, Is.EqualTo(@"C:\proj\Rhino\Tables\Matches\PvPTable.g.cs"));
    }

    [Test]
    public void ComputeOutputPath_TwoFilesInTheSameNestedSubdirectory_EachMirrorsCorrectly() {
        var sourceDir = @"C:\proj\RhinoContracts\Tables";
        var outputDir = @"C:\proj\Rhino\Tables";

        var pvp = ShorthandExpansionRunner.ComputeOutputPath(sourceDir, outputDir, @"C:\proj\RhinoContracts\Tables\Matches\PvPTable.cs");
        var pve = ShorthandExpansionRunner.ComputeOutputPath(sourceDir, outputDir, @"C:\proj\RhinoContracts\Tables\Matches\PvETable.cs");

        Assert.That(pvp, Is.EqualTo(@"C:\proj\Rhino\Tables\Matches\PvPTable.g.cs"));
        Assert.That(pve, Is.EqualTo(@"C:\proj\Rhino\Tables\Matches\PvETable.g.cs"));
    }

    [Test]
    public void ComputeOutputPath_MultiLevelNesting_MirrorsAllLevels() {
        var sourceDir = @"C:\proj\RhinoContracts\Tables";
        var outputDir = @"C:\proj\Rhino\Tables";
        var sourceFile = @"C:\proj\RhinoContracts\Tables\Matches\Ranked\PvPTable.cs";

        var result = ShorthandExpansionRunner.ComputeOutputPath(sourceDir, outputDir, sourceFile);

        Assert.That(result, Is.EqualTo(@"C:\proj\Rhino\Tables\Matches\Ranked\PvPTable.g.cs"));
    }
}
