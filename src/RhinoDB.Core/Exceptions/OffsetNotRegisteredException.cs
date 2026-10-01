namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class OffsetNotRegisteredException(Exception? inner = null) : Exception("Offset is not registered under the given key", inner);
