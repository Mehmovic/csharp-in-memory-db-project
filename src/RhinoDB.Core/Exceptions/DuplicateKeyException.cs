namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class DuplicateKeyException() : Exception("Key already exists");
