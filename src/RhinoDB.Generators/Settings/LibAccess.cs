using Microsoft.CodeAnalysis;

namespace RhinoDB.Generators;

static internal class LibAccess {
    static public IncrementalValueProvider<string> OverrideModifier(IncrementalGeneratorInitializationContext context) =>
        context.CompilationProvider.Select(static (compilation, _) => {
            var lib = compilation.GetTypeByMetadataName("RhinoDB.Lib.Execution.DbContext`1")?.ContainingAssembly;
            return lib is not null && lib.GivesAccessTo(compilation.Assembly) ? "protected internal" : "protected";
        });
}
