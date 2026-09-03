namespace RhinoDB.Core.Exceptions;

[GeneratesRhinoError]
public sealed class OffsetNotRegisteredException(object? key, int? offset)
    : Exception($"Offset {offset?.ToString() ?? "[null]"} is not registered under key '{key ?? "[null]"}'") {
    public object? Key { get; } = key;
    public int? Offset { get; } = offset;
}
