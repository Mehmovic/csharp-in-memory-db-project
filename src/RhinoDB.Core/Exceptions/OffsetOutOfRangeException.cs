namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class OffsetOutOfRangeException(Exception? inner = null) : Exception("Offset is out of range", inner);
