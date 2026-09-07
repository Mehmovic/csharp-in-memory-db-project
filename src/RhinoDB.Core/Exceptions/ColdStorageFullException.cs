namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class ColdStorageFullException() : Exception("Cold storage is full");
