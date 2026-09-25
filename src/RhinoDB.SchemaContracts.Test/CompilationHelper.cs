using System.Collections.Immutable;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace RhinoDB.SchemaContracts.Test;

// Minimal in-memory compilation over a source snippet, purely to get real Roslyn symbols
// (INamedTypeSymbol/IParameterSymbol) to test SchemaContracts' pure symbol-walking logic against -
// mirrors GeneratorTestHost.cs's own TRUSTED_PLATFORM_ASSEMBLIES approach, minus anything
// RhinoDB-specific (fixtures declare their own stub [CustomType]/[MemoryPackable]/[MessagePackObject]
// attributes directly in source, matching the exact fully-qualified names SchemaWalk checks for).
static internal class CompilationHelper {
    static readonly ImmutableArray<MetadataReference> References = BuildReferences();

    static public INamedTypeSymbol GetType(string source, string fullyQualifiedTypeName) {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest));
        var compilation = CSharpCompilation.Create("SchemaContractsTestAssembly", [tree], References, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return compilation.GetTypeByMetadataName(fullyQualifiedTypeName)
            ?? throw new InvalidOperationException($"Type '{fullyQualifiedTypeName}' not found in the compiled source.");
    }

    static ImmutableArray<MetadataReference> BuildReferences() {
        var trustedPlatformAssemblies = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        return trustedPlatformAssemblies.Select(path => (MetadataReference)MetadataReference.CreateFromFile(path)).ToImmutableArray();
    }
}
