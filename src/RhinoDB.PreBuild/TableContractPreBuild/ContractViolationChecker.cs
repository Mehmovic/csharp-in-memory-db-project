using System.Collections.Immutable;

namespace RhinoDB.PreBuild;

static public class ContractViolationChecker {
    static public void Check(ParsedShorthandFile oldFile, ParsedShorthandFile newFile) {
        var oldSlots = ResolvedFieldsBySlot(oldFile);
        var newSlots = ResolvedFieldsBySlot(newFile);

        foreach (var kvp in newSlots) {
            var slot = kvp.Key;
            var newField = kvp.Value;
            if (!oldSlots.TryGetValue(slot, out var oldField)) continue;

            if (oldField.TypeName != newField.TypeName) {
                throw new ContractViolationException(
                    $"'{newFile.TypeName}''s PackId slot {slot} changed type from '{oldField.TypeName}' (field '{oldField.Name}') " +
                    $"to '{newField.TypeName}' (field '{newField.Name}') - this is a wire-format-breaking change.");
            }

            if (!newField.IsExplicit && oldField.Name != newField.Name) {
                throw new ContractViolationException(
                    $"'{newFile.TypeName}''s PackId slot {slot} auto-resolved to a different field: was '{oldField.Name}', " +
                    $"now '{newField.Name}'. If this rename was intentional, pin it explicitly with [PackId({slot})].");
            }
        }
    }

    static ImmutableDictionary<byte, (string Name, string TypeName, bool IsExplicit)> ResolvedFieldsBySlot(ParsedShorthandFile file) {
        var slots = PackIdNumberer.Assign(file.Fields);
        var builder = ImmutableDictionary.CreateBuilder<byte, (string, string, bool)>();
        for (var i = 0; i < file.Fields.Length; i++) {
            var field = file.Fields[i];
            builder[slots[i]] = (field.Name, field.TypeName, field.ExplicitPackId is not null);
        }
        return builder.ToImmutable();
    }
}
