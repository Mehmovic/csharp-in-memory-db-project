using System.Collections.Immutable;
using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RhinoDB.SchemaContracts;

namespace RhinoDB.Tools.Migration;

// Builds a DatabaseContractDescriptor directly from a loaded Compilation - the CLI's counterpart to
// TableGenerator's ForAttributeWithMetadataName-driven discovery, since this runs outside the incremental
// generator pipeline (a one-shot semantic walk over an MSBuildWorkspace-loaded project, not a live IDE/
// build session). Reuses SchemaWalk/DescriptorBuilder from RhinoDB.SchemaContracts - the exact same
// field-flattening logic TableGenerator itself uses for RHINO019, so a table's classification here always
// agrees with what the generator would compute for the same source.
static public class CompilationWalker {
    const string DatabaseAttributeFullName = "RhinoDB.Core.Tables.DatabaseAttribute";
    const string TableAttributeFullName = "RhinoDB.Core.Tables.TableAttribute";
    const string PrimaryKeyAttributeFullName = "RhinoDB.Core.Tables.PrimaryKeyAttribute";

    static public DatabaseContractDescriptor BuildDescriptor(Compilation compilation) {
        var types = AllNamedTypes(compilation).ToList();

        var databases = types
            .Where(t => t.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == DatabaseAttributeFullName))
            .Select(t => new DatabaseGenerationState { FullName = t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) })
            .ToList();

        var tables = new List<TableDescriptor>();
        foreach (var type in types) {
            var tableAttributes = type.GetAttributes().Where(a => a.AttributeClass?.ToDisplayString() == TableAttributeFullName).ToImmutableArray();
            if (tableAttributes.Length == 0) continue;

            var primaryCtor = SchemaWalk.FindPrimaryConstructor(type);
            if (primaryCtor is null) continue;

            var primaryKeyParam = primaryCtor.Parameters.FirstOrDefault(p =>
                p.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == PrimaryKeyAttributeFullName));
            if (primaryKeyParam is null) continue;

            foreach (var attribute in tableAttributes) {
                var kind = (int)attribute.ConstructorArguments[0].Value! == 0 ? "Instant" : "Persistent";
                var databaseType = (INamedTypeSymbol)attribute.ConstructorArguments[1].Value!;
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

    // A partial type's declaration can span several syntax trees (e.g. a shorthand-expanded row type and,
    // under MSBuildWorkspace specifically, a duplicate Compile-item inclusion of its own generated output -
    // confirmed by a real smoke test against RhinoDB.Run.Server.Sandbox) - dedupe by symbol identity so one
    // logical type is never counted twice regardless of how many syntax nodes declare it.
    static IEnumerable<INamedTypeSymbol> AllNamedTypes(Compilation compilation) {
        var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var tree in compilation.SyntaxTrees) {
            var model = compilation.GetSemanticModel(tree);
            foreach (var node in tree.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
                if (model.GetDeclaredSymbol(node) is INamedTypeSymbol symbol && seen.Add(symbol))
                    yield return symbol;
        }
    }

    static string? StringNamedArg(AttributeData attribute, string name) {
        foreach (var kv in attribute.NamedArguments)
            if (kv.Key == name)
                return (string?)kv.Value.Value;
        return null;
    }

    static uint ComputeTableId(string accessor) =>
        Encoding.UTF8.GetBytes(accessor).Aggregate(2166136261u, (current, b) => (current ^ b) * 16777619u);
}
