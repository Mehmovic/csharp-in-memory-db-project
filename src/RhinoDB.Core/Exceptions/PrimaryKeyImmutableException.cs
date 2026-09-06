namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class PrimaryKeyImmutableException() : Exception("Primary key is immutable");
