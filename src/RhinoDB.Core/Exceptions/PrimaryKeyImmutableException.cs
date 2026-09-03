namespace RhinoDB.Core.Exceptions;

[GeneratesRhinoError]
public sealed class PrimaryKeyImmutableException(object? key, object? attemptedKey)
    : Exception($"Primary key is immutable: cannot change '{key ?? "[null]"}' to '{attemptedKey ?? "[null]"}'") {
    public object? Key { get; } = key;
    public object? AttemptedKey { get; } = attemptedKey;
}
