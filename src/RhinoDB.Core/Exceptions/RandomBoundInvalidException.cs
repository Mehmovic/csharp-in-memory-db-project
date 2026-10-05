namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class RandomBoundInvalidException(Exception? inner = null)
    : Exception("A random draw needs a non-empty range: maxExclusive must be positive, and greater than minInclusive.", inner);
