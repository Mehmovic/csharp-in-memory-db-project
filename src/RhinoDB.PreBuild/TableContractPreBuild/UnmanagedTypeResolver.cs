using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RhinoDB.PreBuild;

static public class UnmanagedTypeResolver {
    static readonly ImmutableHashSet<string> BuiltInUnmanagedTypeNames = ImmutableHashSet.Create(
        "sbyte", "byte", "short", "ushort", "int", "uint", "long", "ulong", "nint", "nuint",
        "char", "float", "double", "decimal", "bool",
        "Guid", "System.Guid",
        "DateTime", "System.DateTime",
        "DateTimeOffset", "System.DateTimeOffset",
        "TimeSpan", "System.TimeSpan"
    );

    static public bool IsUnmanaged(string typeName, IReadOnlyDictionary<string, string> allProjectSourceTextsByPath) =>
        IsUnmanaged(typeName, allProjectSourceTextsByPath, new HashSet<string>());

    static bool IsUnmanaged(string typeName, IReadOnlyDictionary<string, string> sources, HashSet<string> visiting) {
        typeName = typeName.Trim();
        if (typeName.EndsWith("?", StringComparison.Ordinal)) return IsUnmanaged(typeName.Substring(0, typeName.Length - 1), sources, visiting);
        if (BuiltInUnmanagedTypeNames.Contains(typeName)) return true;
        if (!visiting.Add(typeName)) return false;
        try {
            var declaration = FindTypeDeclaration(typeName, sources);
            return declaration switch {
                EnumDeclarationSyntax => true,
                RecordDeclarationSyntax { ParameterList: { } parameters } =>
                    parameters.Parameters.All(p => IsUnmanaged(p.Type!.ToString(), sources, visiting)),
                _ => false
            };
        } finally {
            visiting.Remove(typeName);
        }
    }

    static MemberDeclarationSyntax? FindTypeDeclaration(string simpleName, IReadOnlyDictionary<string, string> sources) {
        foreach (var text in sources.Values) {
            var root = CSharpSyntaxTree.ParseText(text).GetCompilationUnitRoot();

            var enumMatch = root.DescendantNodes().OfType<EnumDeclarationSyntax>()
                .FirstOrDefault(e => e.Identifier.Text == simpleName);
            if (enumMatch is not null) return enumMatch;

            var recordMatch = root.DescendantNodes().OfType<RecordDeclarationSyntax>()
                .FirstOrDefault(r => r.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) && r.Identifier.Text == simpleName);
            if (recordMatch is not null) return recordMatch;
        }

        return null;
    }
}
