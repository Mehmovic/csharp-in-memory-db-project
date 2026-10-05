using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RhinoDB.SchemaContracts;

namespace RhinoDB.Tools.Migration;

static public class CompilationWalker {
    private const string DatabaseAttributeFullName = "RhinoDB.Core.Tables.DatabaseAttribute";
    private const string TableAttributeFullName = "RhinoDB.Core.Tables.TableAttribute";
    private const string PrimaryKeyAttributeFullName = "RhinoDB.Core.Tables.PrimaryKeyAttribute";

    static public DatabaseContractDescriptor BuildDescriptor(Compilation compilation) {
        var types = AllNamedTypes(compilation).ToList();

        var databases = types
            .Where(t => t.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == DatabaseAttributeFullName))
            .Select(t => new DatabaseGenerationState { FullName = t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) })
            .ToList();

        var tables = new List<TableDescriptor>();
        foreach (var type in types) {
            var tableAttributes = type.GetAttributes().Where(a => IsTableAttributeClass(a.AttributeClass)).ToImmutableArray();
            if (tableAttributes.Length == 0) continue;

            var primaryCtor = SchemaWalk.FindPrimaryConstructor(type);
            if (primaryCtor is null) continue;

            var primaryKeyParam = primaryCtor.Parameters.FirstOrDefault(p =>
                p.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == PrimaryKeyAttributeFullName));
            if (primaryKeyParam is null) continue;

            foreach (var attribute in tableAttributes) {
                if (ExplicitDatabaseType(attribute) is not { } databaseType) continue;
                if (attribute.ConstructorArguments.Length == 0 || attribute.ConstructorArguments[0].Value is not int tableKind)
                    throw new InvalidOperationException(
                        $"[Table] on '{type.ToDisplayString()}' didn't bind (missing reference to RhinoDB.Core/RhinoDB.SchemaContracts?) - its schema can't be read.");
                var kind = tableKind == 0 ? "Instant" : "Persistent";
                var accessor = StringNamedArg(attribute, "Accessor") is { Length: > 0 } explicitAccessor ? explicitAccessor : type.Name;

                tables.Add(DescriptorBuilder.BuildTable(
                    databaseType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    accessor,
                    type,
                    primaryKeyParam,
                    primaryCtor.Parameters,
                    kind,
                    ComputeTableId(accessor),
                    revision: 0
                ));
            }
        }

        return new DatabaseContractDescriptor { Databases = databases, Tables = tables };
    }

    static private IEnumerable<INamedTypeSymbol> AllNamedTypes(Compilation compilation) {
        var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var tree in compilation.SyntaxTrees) {
            var model = compilation.GetSemanticModel(tree);
            foreach (var node in tree.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
                if (model.GetDeclaredSymbol(node) is INamedTypeSymbol symbol && seen.Add(symbol))
                    yield return symbol;
        }
    }

    static private string? StringNamedArg(AttributeData attribute, string name) {
        foreach (var kv in attribute.NamedArguments)
            if (kv.Key == name)
                return (string?)kv.Value.Value;
        return null;
    }

    static private uint ComputeTableId(string accessor) => TableIdHash.Compute(accessor);

    static private bool IsTableAttributeClass(INamedTypeSymbol? attributeClass) =>
        attributeClass is { Name: "TableAttribute" } && attributeClass.ContainingNamespace.ToDisplayString() == "RhinoDB.Core.Tables";

    static private INamedTypeSymbol? ExplicitDatabaseType(AttributeData attribute) =>
        attribute.AttributeClass is { IsGenericType: true, TypeArguments.Length: 1 } genericAttributeClass
            ? (INamedTypeSymbol)genericAttributeClass.TypeArguments[0]
            : null;
}
