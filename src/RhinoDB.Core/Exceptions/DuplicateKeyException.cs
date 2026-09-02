namespace RhinoDB.Core.Exceptions;

[GeneratesRhinoError]
public sealed class DuplicateKeyException(object key)
    : Exception($"Key '{key}' already exists") {
    public object Key { get; } = key;
}
