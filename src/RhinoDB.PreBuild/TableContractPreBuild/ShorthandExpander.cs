using System.Collections.Immutable;
using System.Text;

namespace RhinoDB.PreBuild;

static public class ShorthandExpander {
    static readonly ImmutableArray<string> RequiredUsings = ImmutableArray.Create("MemoryPack", "MessagePack", "RhinoDB.Core.Tables");

    static public string Expand(string shorthandSourceText, IReadOnlyDictionary<string, string> allProjectSourceTextsByPath) {
        var parsed = ShorthandParser.Parse(shorthandSourceText);
        return Expand(parsed, allProjectSourceTextsByPath);
    }

    static public string Expand(ParsedShorthandFile parsed, IReadOnlyDictionary<string, string> allProjectSourceTextsByPath) {
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
        sb.AppendLine(isFullyUnmanaged ? "[MemoryPackable]" : "[MemoryPackable(GenerateType.VersionTolerant)]");
        sb.AppendLine("[MessagePackObject]");
        sb.AppendLine($"public readonly partial record struct {parsed.TypeName}(");
        EmitFields(sb, parsed.Fields, slots);
        sb.AppendLine(");");

        return sb.ToString();
    }

    static void EmitUsings(StringBuilder sb, ParsedShorthandFile parsed) {
        var all = RequiredUsings.Concat(parsed.UsingDirectives).Distinct().OrderBy(u => u, StringComparer.Ordinal);
        foreach (var u in all) sb.AppendLine($"using {u};");
    }

    static void EmitTypeAttribute(StringBuilder sb, ParsedShorthandFile parsed) {
        if (parsed.Kind == ShorthandKind.RhinoType) {
            sb.AppendLine("[CustomType]");
            return;
        }

        var kindName = parsed.Kind == ShorthandKind.InstantTable ? "TableKind.Instant" : "TableKind.Persistent";
        var namedArgs = string.Concat(parsed.TableNamedArguments.Select(a => $", {a.Name} = {a.Value}"));
        sb.AppendLine($"[Table({kindName}, typeof({parsed.DatabaseTypeName}){namedArgs})]");
    }

    static void EmitFields(StringBuilder sb, ImmutableArray<ParsedShorthandField> fields, ImmutableArray<byte> slots) {
        for (var i = 0; i < fields.Length; i++) {
            var field = fields[i];
            var passThrough = string.Concat(field.PassThroughAttributes.Select(a => $"{a} "));
            var comma = i < fields.Length - 1 ? "," : "";
            sb.AppendLine($"    {passThrough}[property: MemoryPackOrder({slots[i]})] [property: Key({slots[i]})] {field.TypeName} {field.Name}{comma}");
        }
    }
}
