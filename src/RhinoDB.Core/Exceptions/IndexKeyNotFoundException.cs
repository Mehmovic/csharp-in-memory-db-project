namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class IndexKeyNotFoundException() : Exception("Key does not exist");
