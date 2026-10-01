namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class PrimaryKeyImmutableException(Exception? inner = null) : Exception("Primary key is immutable", inner);
