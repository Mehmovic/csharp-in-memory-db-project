namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class ProcedureArgsInvalidException(Exception? inner = null)
    : Exception("The procedure's arguments could not be decoded - the request body doesn't match the procedure's parameters in the project's client protocol.", inner);
