namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class ApplyFailedButRevertedSuccessfullyException(Exception? inner = null)
    : Exception("Apply threw unexpectedly; the operation was fully reverted and the database is consistent", inner);
