namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class OffsetOutOfRangeException() : Exception("Offset is out of range");
