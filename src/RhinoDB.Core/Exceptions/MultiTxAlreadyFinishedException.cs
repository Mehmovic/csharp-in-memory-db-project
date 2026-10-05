namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class MultiTxAlreadyFinishedException(Exception? inner = null)
    : Exception("This multi-database transaction was already committed or rolled back - it is single-use, start a new one.", inner);
