namespace RhinoDB.Core.Exceptions;

public sealed class DuplicateKeyException(object key)
    : Exception($"Key '{key}' already exists") {
    public object Key { get; } = key;
}
