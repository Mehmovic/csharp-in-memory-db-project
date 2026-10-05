namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class RandomNothingToPickException(Exception? inner = null)
    : Exception("There is nothing to pick from - the span is empty.", inner);
