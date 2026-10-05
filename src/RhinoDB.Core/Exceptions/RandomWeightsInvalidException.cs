namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class RandomWeightsInvalidException(Exception? inner = null)
    : Exception("Weights must be finite and non-negative, and add up to a positive total.", inner);
