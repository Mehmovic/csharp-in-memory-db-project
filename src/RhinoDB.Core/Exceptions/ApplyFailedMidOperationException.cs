namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class ApplyFailedMidOperationException()
    : Exception("Apply threw unexpectedly - state may be partially applied, so the database refuses further operations until restarted");
