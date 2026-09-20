using System.Collections.Immutable;

namespace RhinoDB.PreBuild;

public sealed class ParsedShorthandField(
    string name,
    string typeName,
    ImmutableArray<string> passThroughAttributes,
    int? explicitPackId
) {
    public string Name { get; } = name;
    public string TypeName { get; } = typeName;
    public ImmutableArray<string> PassThroughAttributes { get; } = passThroughAttributes;
    public int? ExplicitPackId { get; } = explicitPackId;
}
