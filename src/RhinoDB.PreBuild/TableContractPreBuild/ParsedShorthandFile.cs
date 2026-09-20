using System.Collections.Immutable;

namespace RhinoDB.PreBuild;

public sealed class ParsedShorthandFile(
    string? @namespace,
    ImmutableArray<string> usingDirectives,
    string typeName,
    ShorthandKind kind,
    string? databaseTypeName,
    ImmutableArray<(string Name, string Value)> tableNamedArguments,
    ImmutableArray<ParsedShorthandField> fields
) {
    public string? Namespace { get; } = @namespace;
    public ImmutableArray<string> UsingDirectives { get; } = usingDirectives;
    public string TypeName { get; } = typeName;
    public ShorthandKind Kind { get; } = kind;
    public string? DatabaseTypeName { get; } = databaseTypeName;
    public ImmutableArray<(string Name, string Value)> TableNamedArguments { get; } = tableNamedArguments;
    public ImmutableArray<ParsedShorthandField> Fields { get; } = fields;
}
