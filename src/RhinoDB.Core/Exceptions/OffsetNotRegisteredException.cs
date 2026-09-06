namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class OffsetNotRegisteredException() : Exception("Offset is not registered under the given key");
