namespace RhinoDB.Core.Exceptions;

[GeneratesRhinoError]
public sealed class OffsetOutOfRangeException(int? offset)
    : Exception($"Offset {offset?.ToString() ?? "[null]"} is out of range") {
    public int? Offset { get; } = offset;
}
