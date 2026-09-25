using System.Collections.Immutable;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using RhinoDB.Core.Tables;

namespace RhinoDB.Tools.Migration.Test;

// Minimal in-memory compilation over a source snippet using the REAL RhinoDB.Core.Tables attributes
// (not stubs) - CompilationWalker's own checks are name-based like SchemaWalk's, but referencing the real
// assembly directly is simpler here than re-declaring the attributes, since this test project already has
// a normal ProjectReference to RhinoDB.Core.
static internal class CompilationHelper {
    static readonly ImmutableArray<MetadataReference> References = BuildReferences();

    static public Compilation Compile(string source) {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest));
        return CSharpCompilation.Create("MigrationToolsTestAssembly", [tree], References, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    static ImmutableArray<MetadataReference> BuildReferences() {
        var trustedPlatformAssemblies = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var references = trustedPlatformAssemblies.Select(path => (MetadataReference)MetadataReference.CreateFromFile(path)).ToList();
        references.Add(MetadataReference.CreateFromFile(typeof(TableAttribute).Assembly.Location));
        return references.ToImmutableArray();
    }
}
