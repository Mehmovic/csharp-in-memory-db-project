namespace RhinoDB.Core.Exceptions;

public sealed class DuplicateKeyException : Exception {
    public object Key { get; }

    public DuplicateKeyException(object key)
        : base($"Key '{key}' already exists") {
        Key = key;
    }
}
