using System.Collections.Immutable;

namespace RhinoDB.SchemaContracts;

public sealed class DatabaseModel(string fullName, string simpleName, string? @namespace, ImmutableArray<int> invalidGenerations) {
    public string FullName { get; } = fullName;
    public string SimpleName { get; } = simpleName;
    public string? Namespace { get; } = @namespace;
    public ImmutableArray<int> InvalidGenerations { get; } = invalidGenerations;
}
