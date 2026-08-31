namespace RhinoDB.Core.Exceptions;

public sealed class IndexKeyNotFoundException : Exception {
    public object Key { get; }

    public IndexKeyNotFoundException(object key)
        : base($"Key '{key}' does not exist") {
        Key = key;
    }
}
