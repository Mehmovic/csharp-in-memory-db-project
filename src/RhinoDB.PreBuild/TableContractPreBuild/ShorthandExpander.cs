using System.Collections.Immutable;
using System.Text;

using RhinoDB.SchemaContracts;

namespace RhinoDB.PreBuild;

static public class ShorthandExpander {
    static private readonly ImmutableArray<string> RequiredUsings = ImmutableArray.Create("MemoryPack", "MessagePack", "RhinoDB.Core.Tables");

    static public string Expand(string shorthandSourceText, IReadOnlyDictionary<string, string> allProjectSourceTextsByPath, ClientProtocolKind clientProtocol) {
        var parsed = ShorthandParser.Parse(shorthandSourceText);
        return Expand(parsed, allProjectSourceTextsByPath, clientProtocol);
    }

    static public string Expand(ParsedShorthandFile parsed, IReadOnlyDictionary<string, string> allProjectSourceTextsByPath, ClientProtocolKind clientProtocol) {
        var slots = PackIdNumberer.Assign(parsed.Fields);
        var isFullyUnmanaged = parsed.Fields.All(f => UnmanagedTypeResolver.IsUnmanaged(f.TypeName, allProjectSourceTextsByPath));

        var sb = new StringBuilder();
        EmitUsings(sb, parsed);
        sb.AppendLine();

        if (parsed.Namespace is not null) {
            sb.AppendLine($"namespace {parsed.Namespace};");
            sb.AppendLine();
        }

        EmitTypeAttribute(sb, parsed);
        if (clientProtocol == ClientProtocolKind.VersionedMemoryPack)
            sb.AppendLine(isFullyUnmanaged ? "[MemoryPackable]" : "[MemoryPackable(GenerateType.VersionTolerant)]");
        if (clientProtocol == ClientProtocolKind.MessagePack)
            sb.AppendLine("[MessagePackObject]");
        sb.AppendLine($"public readonly partial record struct {parsed.TypeName}(");
        EmitFields(sb, parsed.Fields, slots);
        sb.AppendLine(");");

        return sb.ToString();
    }

    static private void EmitUsings(StringBuilder sb, ParsedShorthandFile parsed) {
        var all = RequiredUsings.Concat(parsed.UsingDirectives).Distinct().OrderBy(u => u, StringComparer.Ordinal);
        foreach (var u in all) sb.AppendLine($"using {u};");
    }

    static private void EmitTypeAttribute(StringBuilder sb, ParsedShorthandFile parsed) {
        if (parsed.Kind == ShorthandKind.RhinoType) {
            sb.AppendLine("[CustomType]");
            return;
        }

        var kindName = parsed.Kind == ShorthandKind.InstantTable ? "TableKind.Instant" : "TableKind.Persistent";
        var namedArgs = string.Concat(parsed.TableNamedArguments.Select(a => $", {a.Name} = {a.Value}"));
        sb.AppendLine($"[Table<{parsed.DatabaseTypeName}>({kindName}{namedArgs})]");
    }

    static private void EmitFields(StringBuilder sb, ImmutableArray<ParsedShorthandField> fields, ImmutableArray<byte> slots) {
        for (var i = 0; i < fields.Length; i++) {
            var field = fields[i];
            var passThrough = string.Concat(field.PassThroughAttributes.Select(a => $"{a} "));
            var comma = i < fields.Length - 1 ? "," : "";
            sb.AppendLine($"    {passThrough}[property: MemoryPackOrder({slots[i]})] [property: Key({slots[i]})] {field.TypeName} {field.Name}{comma}");
        }
    }
}
