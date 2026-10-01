namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class IndexKeyNotFoundException(Exception? inner = null) : Exception("Key does not exist", inner);
