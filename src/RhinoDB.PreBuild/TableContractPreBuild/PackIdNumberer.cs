using System.Collections.Immutable;

namespace RhinoDB.PreBuild;

static public class PackIdNumberer {
    static public ImmutableArray<int> Assign(ImmutableArray<ParsedShorthandField> fields) {
        var raw = new int[fields.Length];

        var anchorIndex = -1;
        for (var i = 0; i < fields.Length; i++) {
            if (fields[i].ExplicitPackId is { } value) {
                anchorIndex = i;
                raw[i] = value;
                break;
            }
        }

        if (anchorIndex < 0) {
            for (var i = 0; i < fields.Length; i++) raw[i] = i;
        } else {
            for (var i = anchorIndex - 1; i >= 0; i--) raw[i] = raw[i + 1] - 1;
            for (var i = anchorIndex + 1; i < fields.Length; i++)
                raw[i] = fields[i].ExplicitPackId is { } explicitValue ? explicitValue : raw[i - 1] + 1;
        }

        CheckForCollisions(fields, raw);

        var min = raw.Length == 0 ? 0 : raw.Min();
        return raw.Select(v => v - min).ToImmutableArray();
    }

    static void CheckForCollisions(ImmutableArray<ParsedShorthandField> fields, int[] raw) {
        var seen = new Dictionary<int, string>();
        foreach (var (field, value) in fields.Zip(raw, (f, v) => (f, v))) {
            if (seen.TryGetValue(value, out var existingFieldName))
                throw new PackIdCollisionException($"Fields '{existingFieldName}' and '{field.Name}' both resolve to PackId slot {value}.");

            seen[value] = field.Name;
        }
    }
}
