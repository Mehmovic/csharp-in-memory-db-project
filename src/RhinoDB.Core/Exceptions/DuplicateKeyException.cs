namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class DuplicateKeyException(Exception? inner = null) : Exception("Key already exists", inner);
