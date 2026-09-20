using System.Text.RegularExpressions;
using RhinoDB.PreBuild;

namespace RhinoDB.Test.Generators;

// Proves ShorthandExpander's output is accepted by TableGenerator/CustomTypeGenerator - real attributed
// source with no RHINO015/016/017 diagnostics, not a snapshot/string comparison. Deliberately uses plain
// CompileAndLoad, not CompileAndLoadWithSerializationGenerators: the latter hits the same pre-existing
// MemoryPack.Generator/.NET 11 preview SDK generic-constraint incompatibility (CS0425/CS0452) already
// documented and explicitly accepted as out of scope during task #82 (see the plan's Part A note on
// CompileAndLoadWithSerializationGenerators) - confirmed here again, not a new bug in ShorthandExpander's
// output, by hitting the identical CS0425/CS0452 errors on a fully-unmanaged shorthand row too.
//
// Real usage writes each expanded type to its own physical file (each with its own using/namespace
// header - that's exactly what makes the expansion "real, hand-writable-looking source"). These tests
// need several expanded types in ONE compilation unit string, which raw concatenation can't do (a
// file-scoped `namespace X;` can only appear once per file, and using directives can't follow it) -
// ComposeSingleCompilationUnit below merges multiple Expand() outputs into one valid file purely for
// this test harness's benefit, not a real constraint on the tool itself.
public class ShorthandExpanderTableGeneratorAcceptanceTests {
    static readonly Dictionary<string, string> NoProjectSources = new();

    const string DatabaseBoilerplate =
        "using RhinoDB.Lib.Execution;\n\n[Database]\npublic partial class GameDb : DbContext<GameDbTransaction> { }\n";

    [Test]
    public void Expand_TableWithAReferenceTypedField_TableGeneratorAcceptsTheExpandedSourceWithNoDiagnostics() {
        var expanded = ShorthandExpander.Expand("""
            namespace TestNs;

            [InstantTable(typeof(GameDb))]
            public readonly partial record struct ShorthandMetric(
                [PrimaryKey] int Id,
                string Label
            );
            """, NoProjectSources);

        var (assembly, _) = GeneratorTestHost.CompileAndLoad(
            ComposeSingleCompilationUnit(DatabaseBoilerplate, expanded));

        Assert.That(assembly.GetType("TestNs.ShorthandMetric"), Is.Not.Null);
    }

    [Test]
    public void Expand_FullyUnmanagedTable_TableGeneratorAcceptsPlainMemoryPackable() {
        var expanded = ShorthandExpander.Expand("""
            namespace TestNs;

            [InstantTable(typeof(GameDb))]
            public readonly partial record struct ShorthandPoint(
                [PrimaryKey] int Id,
                int X,
                int Y
            );
            """, NoProjectSources);

        var (assembly, _) = GeneratorTestHost.CompileAndLoad(
            ComposeSingleCompilationUnit(DatabaseBoilerplate, expanded));

        Assert.That(assembly.GetType("TestNs.ShorthandPoint"), Is.Not.Null);
    }

    [Test]
    public void Expand_RhinoTypeNestedInsideAShorthandTableField_TableGeneratorAcceptsBothExpandedTypes() {
        var customTypeSource = """
            namespace TestNs;

            [RhinoType]
            public readonly partial record struct ShorthandPlayerName(
                string First,
                string Last
            );
            """;
        var expandedCustomType = ShorthandExpander.Expand(customTypeSource, NoProjectSources);

        var projectSources = new Dictionary<string, string> { ["PlayerName.rhinotype"] = customTypeSource };
        var expandedTable = ShorthandExpander.Expand("""
            namespace TestNs;

            [InstantTable(typeof(GameDb))]
            public readonly partial record struct ShorthandPlayer(
                [PrimaryKey] int Id,
                ShorthandPlayerName Name
            );
            """, projectSources);

        var (assembly, _) = GeneratorTestHost.CompileAndLoad(
            ComposeSingleCompilationUnit(DatabaseBoilerplate, expandedCustomType, expandedTable));

        Assert.That(assembly.GetType("TestNs.ShorthandPlayer"), Is.Not.Null);
        Assert.That(assembly.GetType("TestNs.ShorthandPlayerName"), Is.Not.Null);
    }

    // Takes N independently-valid "files" (each with its own using directives and file-scoped
    // namespace), and merges them into one compilation unit: usings deduplicated and hoisted to the
    // top, exactly one namespace declaration, all type bodies concatenated beneath it.
    static string ComposeSingleCompilationUnit(params string[] units) {
        var usings = new List<string>();
        var bodies = new List<string>();
        string? ns = null;

        foreach (var rawUnit in units) {
            var unit = rawUnit.Replace("\r\n", "\n");
            foreach (Match m in Regex.Matches(unit, @"^using ([^;]+);$", RegexOptions.Multiline))
                if (!usings.Contains(m.Groups[1].Value)) usings.Add(m.Groups[1].Value);

            var nsMatch = Regex.Match(unit, @"^namespace ([^;{]+);?", RegexOptions.Multiline);
            if (nsMatch.Success) ns = nsMatch.Groups[1].Value.Trim();

            var body = Regex.Replace(unit, @"^using [^;]+;$", "", RegexOptions.Multiline);
            body = Regex.Replace(body, @"^namespace [^;{]+;?", "", RegexOptions.Multiline);
            bodies.Add(body.Trim());
        }

        var usingLines = string.Join("\n", usings.Select(u => $"using {u};"));
        var nsLine = ns is null ? "" : $"namespace {ns};\n";
        return $"{usingLines}\n\n{nsLine}\n{string.Join("\n\n", bodies)}\n";
    }
}
