namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class ApplyFailedException(Exception? inner = null)
    : Exception("Apply threw and the operation could not be fully reverted - state is unknown, so the database refuses further operations until restarted", inner);
