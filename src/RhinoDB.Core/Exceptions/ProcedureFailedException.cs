namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class ProcedureFailedException(Exception? inner = null)
    : Exception("The procedure threw instead of returning a Result - a bug in procedure code. The exception is logged on the server and never sent to the client.", inner);
