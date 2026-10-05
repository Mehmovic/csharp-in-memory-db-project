using System.Collections.Immutable;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RhinoDB.SchemaContracts;

static public class DatabaseDiscovery {
    public const string DatabaseAttributeFullName = "RhinoDB.Core.Tables.DatabaseAttribute";
    public const string ChildDatabaseAttributeFullName = "RhinoDB.Core.Tables.ChildDatabaseAttribute`2";
    public const string SingletonChildDatabaseAttributeFullName = "RhinoDB.Core.Tables.ChildDatabaseAttribute`1";

    static public IncrementalValuesProvider<T> ChildDatabases<T>(
        IncrementalGeneratorInitializationContext context, Func<GeneratorAttributeSyntaxContext, CancellationToken, T> transform) {
        var keyed = context.SyntaxProvider.ForAttributeWithMetadataName(
            ChildDatabaseAttributeFullName, static (node, _) => node is ClassDeclarationSyntax, transform);
        var singleton = context.SyntaxProvider.ForAttributeWithMetadataName(
            SingletonChildDatabaseAttributeFullName, static (node, _) => node is ClassDeclarationSyntax, transform);
        return keyed.Collect().Combine(singleton.Collect()).SelectMany(static (pair, _) => pair.Left.AddRange(pair.Right));
    }

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

    // KeyFullName is null for a singleton Child.
    static public (string RootFullName, string? KeyFullName) ToChildDatabaseTypeArgs(GeneratorAttributeSyntaxContext ctx) {
        var attributeClass = (INamedTypeSymbol)ctx.Attributes[0].AttributeClass!;
        return (
            attributeClass.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            attributeClass.TypeArguments.Length > 1 ? attributeClass.TypeArguments[1].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) : null
        );
    }

    static private ImmutableArray<int> IntArrayNamedArg(AttributeData attr, string name) {
        foreach (var kv in attr.NamedArguments)
            if (kv.Key == name)
                return kv.Value.IsNull ? ImmutableArray<int>.Empty : [..kv.Value.Values.Select(v => (int)v.Value!)];
        return ImmutableArray<int>.Empty;
    }
}
