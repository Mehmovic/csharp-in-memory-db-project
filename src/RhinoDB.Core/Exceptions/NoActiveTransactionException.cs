namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class NoActiveTransactionException(Exception? inner = null)
    : Exception("Persistent table writes must happen inside DbContext.Run - no active transaction is reachable here", inner);
