using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RhinoDB.PreBuild;

static public class ShorthandParser {
    static public ParsedShorthandFile Parse(string sourceText) {
        var root = CSharpSyntaxTree.ParseText(sourceText).GetCompilationUnitRoot();

        var usings = root.DescendantNodes()
            .OfType<UsingDirectiveSyntax>()
            .Select(u => u.Name!.ToString())
            .ToImmutableArray();

        var @namespace = root.Members
            .OfType<BaseNamespaceDeclarationSyntax>()
            .FirstOrDefault()?.Name.ToString();

        var records = root.DescendantNodes()
            .OfType<RecordDeclarationSyntax>()
            .Where(r => r.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword))
            .ToImmutableArray();

        if (records.Length == 0) throw new ShorthandParseException("No 'record struct' declaration found in shorthand file.");
        if (records.Length > 1) throw new ShorthandParseException("A shorthand file must declare exactly one 'record struct' - found more than one.");

        var record = records[0];
        var typeName = record.Identifier.Text;

        var (kind, databaseTypeName, tableNamedArguments) = ParseShorthandAttribute(record, typeName);

        if (record.ParameterList is null) throw new ShorthandParseException($"'{typeName}' has no primary constructor - shorthand types must declare their fields as primary-constructor parameters.");

        var fields = record.ParameterList.Parameters.Select(ParseField).ToImmutableArray();

        return new ParsedShorthandFile(@namespace, usings, typeName, kind, databaseTypeName, tableNamedArguments, fields);
    }

    static private (ShorthandKind Kind, string? DatabaseTypeName, ImmutableArray<(string Name, string Value)> NamedArguments) ParseShorthandAttribute(
        RecordDeclarationSyntax record, string typeName) {
        foreach (var attrList in record.AttributeLists) {
            foreach (var attr in attrList.Attributes) {
                var simpleName = AttributeSimpleName(attr);
                switch (simpleName) {
                    case "InstantTable": return (ShorthandKind.InstantTable, ParseDatabaseArgument(attr, typeName), ParseNamedArguments(attr));
                    case "PersistentTable": return (ShorthandKind.PersistentTable, ParseDatabaseArgument(attr, typeName), ParseNamedArguments(attr));
                    case "RhinoType": return (ShorthandKind.RhinoType, null, ImmutableArray<(string, string)>.Empty);
                }
            }
        }

        throw new ShorthandParseException($"'{typeName}' has no recognized shorthand attribute - expected [InstantTable], [PersistentTable], or [RhinoType].");
    }

    static private string AttributeSimpleName(AttributeSyntax attr) =>
        attr.Name switch {
            SimpleNameSyntax simple => simple.Identifier.Text,
            QualifiedNameSyntax qualified => qualified.Right.Identifier.Text,
            _ => attr.Name.ToString()
        };

    static private string ParseDatabaseArgument(AttributeSyntax attr, string typeName) {
        if (attr.Name is not GenericNameSyntax { TypeArgumentList.Arguments.Count: 1 } generic)
            throw new ShorthandParseException($"'{typeName}''s shorthand table attribute must name the database as a generic type argument, e.g. [InstantTable<YourDb>].");

        return generic.TypeArgumentList.Arguments[0].ToString();
    }

    static private ImmutableArray<(string Name, string Value)> ParseNamedArguments(AttributeSyntax attr) {
        if (attr.ArgumentList is null) return ImmutableArray<(string, string)>.Empty;

        return attr.ArgumentList.Arguments
            .Where(a => a.NameEquals is not null)
            .Select(a => (a.NameEquals!.Name.Identifier.Text, a.Expression.ToString()))
            .ToImmutableArray();
    }

    static private ParsedShorthandField ParseField(ParameterSyntax parameter) {
        var typeName = parameter.Type?.ToString() ?? "";
        if (parameter.Identifier.IsMissing || string.IsNullOrWhiteSpace(typeName))
            throw new ShorthandParseException(
                "Found a malformed field declaration in the parameter list (check for a trailing comma or a missing type/name).");

        var name = parameter.Identifier.Text;

        int? explicitPackId = null;
        var passThrough = ImmutableArray.CreateBuilder<string>();

        foreach (var attrList in parameter.AttributeLists) {
            foreach (var attr in attrList.Attributes) {
                if (AttributeSimpleName(attr) == "PackId") {
                    explicitPackId = ParsePackIdValue(attr, name);
                } else {
                    passThrough.Add($"[{attr}]");
                }
            }
        }

        return new ParsedShorthandField(name, typeName, passThrough.ToImmutable(), explicitPackId);
    }

    static private int ParsePackIdValue(AttributeSyntax attr, string fieldName) {
        var arg = attr.ArgumentList?.Arguments.Count == 1 ? attr.ArgumentList.Arguments[0] : null;
        if (arg is null || !TryGetIntLiteral(arg.Expression, out var value))
            throw new ShorthandParseException($"'{fieldName}''s [PackId(n)] must have exactly one integer literal argument.");

        return value;
    }

    static private bool TryGetIntLiteral(ExpressionSyntax expr, out int value) {
        switch (expr) {
            case LiteralExpressionSyntax { Token.Value: int literalValue }:
                value = literalValue;
                return true;
            case PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.UnaryMinusExpression } unary
                when TryGetIntLiteral(unary.Operand, out var inner):
                value = -inner;
                return true;
            default:
                value = 0;
                return false;
        }
    }
}
