using System.Collections.Immutable;
using System.Linq;
using System.Text;

using Microsoft.CodeAnalysis;

namespace RhinoDB.SchemaContracts;

static public class SchemaWalk {
    public const string CustomTypeAttributeFullName = "RhinoDB.Core.Tables.CustomTypeAttribute";

    static public readonly DiagnosticDescriptor MissingSerializationAttributesOnCustomTypeFieldDiagnostic = new(
        "RHINO016",
        "Field's type must be a [CustomType] with mandatory IDC/client serialization attributes",
        "Field '{0}.{1}' has type '{2}', which is missing {3} - a field that isn't unmanaged or string "
        + "must be an explicitly [CustomType]-marked type carrying both [MemoryPackable] and "
        + "[MessagePackObject]; unmarked types are rejected rather than silently falling back to "
        + "MemoryPack's generic WriteValue<T>/ReadValue<T> formatter dispatch at runtime. This check "
        + "applies at every level - a [CustomType]'s own fields are checked the same way when that type "
        + "is itself processed by CustomTypeGenerator.",
        "RhinoDB.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    static private readonly ImmutableHashSet<(string Name, int Arity)> BuiltInFormattedGenericCollections =
        ImmutableHashSet.Create(("List", 1), ("Dictionary", 2), ("HashSet", 1), ("Queue", 1), ("Stack", 1));

    static public bool HasMemoryPackable(INamedTypeSymbol type) =>
        type.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "MemoryPack.MemoryPackableAttribute");

    static public bool HasMessagePackObject(INamedTypeSymbol type) =>
        type.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "MessagePack.MessagePackObjectAttribute");

    static public bool HasCustomTypeAttribute(INamedTypeSymbol type) =>
        type.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == CustomTypeAttributeFullName);

    static public bool IsBuiltInFormattedCollection(INamedTypeSymbol type) =>
        type.OriginalDefinition.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic"
        && BuiltInFormattedGenericCollections.Contains((type.OriginalDefinition.Name, type.OriginalDefinition.Arity));

    // Shared between TableGenerator (row fields) and CustomTypeGenerator (custom type fields) - both
    // walk a primary constructor's parameters into the exact same field shape.
    static public ImmutableArray<RowFieldModel> ToRowFieldModels(ImmutableArray<IParameterSymbol> parameters) =>
        parameters
            .Select(p => new RowFieldModel(
                p.Name,
                p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                p.Type.SpecialType == SpecialType.System_String ? RowFieldKind.String
                    : p.Type.IsUnmanagedType ? RowFieldKind.Unmanaged
                    : RowFieldKind.Other,
                p.Type is INamedTypeSymbol namedType && HasCustomTypeAttribute(namedType)
            ))
            .ToImmutableArray();

    static public ImmutableArray<Diagnostic> CheckOtherKindFieldAttributes(
        ImmutableArray<IParameterSymbol> parameters, string containingTypeName, Location location) {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        foreach (var p in parameters) {
            if (p.Type.SpecialType == SpecialType.System_String || p.Type.IsUnmanagedType) continue;
            if (p.Type is not INamedTypeSymbol fieldType) continue;
            if (IsBuiltInFormattedCollection(fieldType)) continue;

            var missing = ImmutableArray.CreateBuilder<string>();
            if (!HasCustomTypeAttribute(fieldType)) missing.Add("[CustomType]");
            if (!HasMemoryPackable(fieldType)) missing.Add("[MemoryPackable]");
            if (!HasMessagePackObject(fieldType)) missing.Add("[MessagePackObject]");
            if (missing.Count > 0)
                diagnostics.Add(Diagnostic.Create(
                    MissingSerializationAttributesOnCustomTypeFieldDiagnostic, location,
                    containingTypeName, p.Name, fieldType.Name, string.Join(" and ", missing)));
        }
        return diagnostics.ToImmutable();
    }

    static public void EmitWriteField(StringBuilder sb, RowFieldModel field, string expr) {
        switch (field.Kind) {
            case RowFieldKind.Unmanaged: sb.AppendLine($"        writer.WriteUnmanaged({expr});"); break;
            case RowFieldKind.String: sb.AppendLine($"        writer.WriteString({expr});"); break;
            default:
                if (field.IsCustomType) sb.AppendLine($"        {field.FieldTypeFullName}CustomTypeOps.WriteRaw(ref writer, {expr});");
                else sb.AppendLine($"        writer.WriteValue({expr});");
                break;
        }
    }

    static public void EmitReadField(StringBuilder sb, RowFieldModel field) {
        var varName = $"{Camel(field.FieldName)}Value";
        switch (field.Kind) {
            case RowFieldKind.Unmanaged:
                sb.AppendLine($"        reader.ReadUnmanaged<{field.FieldTypeFullName}>(out var {varName});");
                break;
            case RowFieldKind.String:
                sb.AppendLine($"        var {varName} = reader.ReadString()!;");
                break;
            default:
                if (field.IsCustomType) sb.AppendLine($"        var {varName} = {field.FieldTypeFullName}CustomTypeOps.ReadRaw(ref reader);");
                else sb.AppendLine($"        var {varName} = reader.ReadValue<{field.FieldTypeFullName}>();");
                break;
        }
    }

    static private string Camel(string name) => name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name.Substring(1);
}
