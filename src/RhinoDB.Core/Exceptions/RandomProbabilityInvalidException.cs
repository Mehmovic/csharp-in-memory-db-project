namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class RandomProbabilityInvalidException(Exception? inner = null)
    : Exception("A chance needs a probability that is a number (NaN isn't).", inner);
