namespace RhinoDB.Core.Exceptions;

[GeneratesRhinoError]
public sealed class IndexKeyNotFoundException(object key)
    : Exception($"Key '{key}' does not exist") {
    public object Key { get; } = key;
}
