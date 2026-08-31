namespace RhinoDB.Core.Exceptions;

public sealed class OffsetNotRegisteredException : Exception {
    public object Key { get; }
    public int Offset { get; }

    public OffsetNotRegisteredException(object key, int offset)
        : base($"Offset {offset} is not registered under key '{key}'") {
        Key = key;
        Offset = offset;
    }
}
