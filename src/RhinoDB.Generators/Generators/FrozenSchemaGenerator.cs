using System.Collections.Immutable;
using System.Linq;
using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RhinoDB.SchemaContracts;

namespace RhinoDB.Generators;

[Generator]
public sealed class FrozenSchemaGenerator : IIncrementalGenerator {
    private const string FrozenSchemaAttributeFullName = "RhinoDB.Core.Tables.FrozenSchemaAttribute";
    private const string PrimaryKeyAttributeFullName = "RhinoDB.Core.Tables.PrimaryKeyAttribute";

    static private readonly DiagnosticDescriptor MissingPrimaryKeyDiagnostic = new DiagnosticDescriptor(
        "RHINO022",
        "Frozen schema row missing [PrimaryKey]",
        "Row type '{0}' is [FrozenSchema]-attributed but declares no [PrimaryKey] parameter - a frozen "
        + "snapshot needs to know its key field the same way a live [Table] row does, since archived WAL "
        + "entries store Key and Row bytes separately and a frozen type must decode both independently",
        "RhinoDB.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    static private readonly DiagnosticDescriptor MissingSerializationAttributesDiagnostic = new DiagnosticDescriptor(
        "RHINO025",
        "Frozen schema row missing mandatory client-protocol serialization attribute",
        "Row type '{0}' is [FrozenSchema]-attributed but is missing {1} - this project's ClientProtocol "
        + "(rdbsettings.json's Generator section) requires it here too, since CompatAdapter's upgrade path needs to decode "
        + "this old shape in the project's own client wire format, not just Raw",
        "RhinoDB.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public void Initialize(IncrementalGeneratorInitializationContext context) {
        var configProtocol = context.AdditionalTextsProvider
            .Where(static t => Path.GetFileName(t.Path) == "rdbsettings.json")
            .Collect()
            .Select(static (texts, ct) => {
                if (texts.Length == 0) return ClientProtocolKind.Raw;
                var text = texts[0].GetText(ct)?.ToString();
                if (string.IsNullOrEmpty(text)) return ClientProtocolKind.Raw;
                try {
                    var parsedConfig = GeneratorConfigLoader.Parse(text!);
                    return ClientProtocolParser.Parse(parsedConfig.ClientProtocol);
                } catch {
                    return ClientProtocolKind.Raw;
                }
            });

        var results = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                FrozenSchemaAttributeFullName,
                predicate: static (node, _) => node is StructDeclarationSyntax or RecordDeclarationSyntax,
                transform: static (ctx, _) => ctx
            )
            .Combine(configProtocol)
            .Select(static (pair, _) => ToFrozenSchemaModel(pair.Left, pair.Right));

        context.RegisterSourceOutput(results, static (spc, result) => {
            foreach (var diagnostic in result.Diagnostics) spc.ReportDiagnostic(diagnostic);
            if (result.Model is not null) spc.AddSource($"{result.Model.SimpleName}.FrozenSchema.g.cs", Emit(result.Model, result.ClientProtocol));
        });
    }

    static private (FrozenSchemaModel? Model, ImmutableArray<Diagnostic> Diagnostics, ClientProtocolKind ClientProtocol) ToFrozenSchemaModel(
        GeneratorAttributeSyntaxContext ctx, ClientProtocolKind clientProtocol) {
        var type = (INamedTypeSymbol)ctx.TargetSymbol;
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

        var primaryCtor = SchemaWalk.FindPrimaryConstructor(type);
        var primaryKeyParam = primaryCtor?.Parameters.FirstOrDefault(p =>
            p.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == PrimaryKeyAttributeFullName)
        );
        if (primaryCtor is null || primaryKeyParam is null) {
            diagnostics.Add(Diagnostic.Create(MissingPrimaryKeyDiagnostic, ctx.TargetNode.GetLocation(), type.Name));
            return (null, diagnostics.ToImmutable(), clientProtocol);
        }

        var missingSerializationAttrs = ImmutableArray.CreateBuilder<string>();
        if (clientProtocol == ClientProtocolKind.VersionedMemoryPack && !SchemaWalk.HasMemoryPackable(type)) missingSerializationAttrs.Add("[MemoryPackable]");
        if (clientProtocol == ClientProtocolKind.MessagePack && !SchemaWalk.HasMessagePackObject(type)) missingSerializationAttrs.Add("[MessagePackObject]");
        if (missingSerializationAttrs.Count > 0) {
            diagnostics.Add(Diagnostic.Create(
                MissingSerializationAttributesDiagnostic, ctx.TargetNode.GetLocation(), type.Name, string.Join(" and ", missingSerializationAttrs)));
            return (null, diagnostics.ToImmutable(), clientProtocol);
        }

        diagnostics.AddRange(SchemaWalk.CheckOtherKindFieldAttributes(primaryCtor.Parameters, type.Name, ctx.TargetNode.GetLocation(), clientProtocol));
        if (diagnostics.Count > 0) return (null, diagnostics.ToImmutable(), clientProtocol);

        var fields = SchemaWalk.ToRowFieldModels(primaryCtor.Parameters);
        var revision = (int)ctx.Attributes[0].ConstructorArguments[0].Value!;

        var model = new FrozenSchemaModel(
            type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            type.Name,
            type.ContainingNamespace.IsGlobalNamespace ? null : type.ContainingNamespace.ToDisplayString(),
            revision,
            primaryKeyParam.Name,
            primaryKeyParam.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            fields
        );
        return (model, diagnostics.ToImmutable(), clientProtocol);
    }

    static private string Emit(FrozenSchemaModel model, ClientProtocolKind clientProtocol) {
        var sb = new StringBuilder();
        var row = model.FullName;
        var key = model.PrimaryKeyTypeFullName;
        var opsName = $"{model.SimpleName}FrozenSchemaOps";
        var pkField = model.Fields.First(f => f.FieldName == model.PrimaryKeyName);

        sb.AppendLine("// <auto-generated>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using MemoryPack;");
        sb.AppendLine("using MessagePack;");
        sb.AppendLine("using RhinoDB.Core;");
        sb.AppendLine();
        if (model.Namespace is not null) {
            sb.AppendLine($"namespace {model.Namespace};");
            sb.AppendLine();
        }

        sb.AppendLine($"public static class {opsName} {{");
        sb.AppendLine($"    public const int Revision = {model.Revision};");
        sb.AppendLine();

        sb.AppendLine($"    internal static byte[] SerializeRow({row} row) {{");
        sb.AppendLine("        var bufferWriter = new PooledBufferWriter(256);");
        sb.AppendLine("        var writerState = MemoryPackWriterOptionalStatePool.Rent(null);");
        sb.AppendLine("        var writer = new MemoryPackWriter<PooledBufferWriter>(ref bufferWriter, writerState);");
        foreach (var f in model.Fields) SchemaWalk.EmitWriteField(sb, f, $"row.{f.FieldName}");
        sb.AppendLine("        writer.Flush();");
        sb.AppendLine("        var result = bufferWriter.WrittenSpan.ToArray();");
        sb.AppendLine("        bufferWriter.Dispose();");
        sb.AppendLine("        return result;");
        sb.AppendLine("    }");
        sb.AppendLine();

        sb.AppendLine($"    internal static {row} DeserializeRow(byte[] bytes) {{");
        sb.AppendLine("        var readerState = MemoryPackReaderOptionalStatePool.Rent(null);");
        sb.AppendLine("        var reader = new MemoryPackReader(bytes, readerState);");
        foreach (var f in model.Fields) SchemaWalk.EmitReadField(sb, f);
        sb.AppendLine("        reader.Dispose();");
        sb.Append($"        return new {row}(");
        sb.Append(string.Join(", ", model.Fields.Select(f => $"{TableGenerator.Camel(f.FieldName)}Value")));
        sb.AppendLine(");");
        sb.AppendLine("    }");
        sb.AppendLine();

        sb.AppendLine($"    internal static byte[] SerializeKey({key} key) {{");
        sb.AppendLine("        var bufferWriter = new PooledBufferWriter(32);");
        sb.AppendLine("        var writerState = MemoryPackWriterOptionalStatePool.Rent(null);");
        sb.AppendLine("        var writer = new MemoryPackWriter<PooledBufferWriter>(ref bufferWriter, writerState);");
        SchemaWalk.EmitWriteField(sb, pkField, "key");
        sb.AppendLine("        writer.Flush();");
        sb.AppendLine("        var result = bufferWriter.WrittenSpan.ToArray();");
        sb.AppendLine("        bufferWriter.Dispose();");
        sb.AppendLine("        return result;");
        sb.AppendLine("    }");
        sb.AppendLine();

        sb.AppendLine($"    internal static {key} DeserializeKey(byte[] bytes) {{");
        sb.AppendLine("        var readerState = MemoryPackReaderOptionalStatePool.Rent(null);");
        sb.AppendLine("        var reader = new MemoryPackReader(bytes, readerState);");
        SchemaWalk.EmitReadField(sb, pkField);
        sb.AppendLine("        reader.Dispose();");
        sb.AppendLine($"        return {TableGenerator.Camel(pkField.FieldName)}Value;");
        sb.AppendLine("    }");
        sb.AppendLine();

        SchemaWalk.EmitProtocolWrapperMethods(sb, row, clientProtocol);

        sb.AppendLine("}");
        return sb.ToString();
    }

    private sealed class FrozenSchemaModel(
        string fullName, string simpleName, string? @namespace, int revision,
        string primaryKeyName, string primaryKeyTypeFullName, ImmutableArray<RowFieldModel> fields
    ) {
        public string FullName { get; } = fullName;
        public string SimpleName { get; } = simpleName;
        public string? Namespace { get; } = @namespace;
        public int Revision { get; } = revision;
        public string PrimaryKeyName { get; } = primaryKeyName;
        public string PrimaryKeyTypeFullName { get; } = primaryKeyTypeFullName;
        public ImmutableArray<RowFieldModel> Fields { get; } = fields;
    }
}
