using System.Collections.Immutable;

using Microsoft.CodeAnalysis;

namespace RhinoDB.SchemaContracts;

static public class DatabaseDiscovery {
    public const string DatabaseAttributeFullName = "RhinoDB.Core.Tables.DatabaseAttribute";

    static public DatabaseModel ToDatabaseModel(GeneratorAttributeSyntaxContext ctx) {
        var databaseType = (INamedTypeSymbol)ctx.TargetSymbol;
        var invalidGenerations = IntArrayNamedArg(ctx.Attributes[0], "InvalidGenerations");
        return new DatabaseModel(
            databaseType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            databaseType.Name,
            databaseType.ContainingNamespace.IsGlobalNamespace ? null : databaseType.ContainingNamespace.ToDisplayString(),
            invalidGenerations
        );
    }

    static private ImmutableArray<int> IntArrayNamedArg(AttributeData attr, string name) {
        foreach (var kv in attr.NamedArguments)
            if (kv.Key == name)
                return kv.Value.IsNull ? ImmutableArray<int>.Empty : [..kv.Value.Values.Select(v => (int)v.Value!)];
        return ImmutableArray<int>.Empty;
    }
}
