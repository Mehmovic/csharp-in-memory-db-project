namespace RhinoDB.Core.Exceptions;

[GeneratesRhinoError]
public sealed class OffsetOutOfRangeException(int offset)
    : Exception($"Offset {offset} is out of range") {
    public int Offset { get; } = offset;
}
