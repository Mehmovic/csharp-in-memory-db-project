namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class ColdStorageFullException(Exception? inner = null) : Exception("Cold storage is full", inner);
