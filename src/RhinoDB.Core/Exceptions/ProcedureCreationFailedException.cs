namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class ProcedureCreationFailedException(Exception? inner = null) : Exception("Creating procedure on execution loop failed.", inner);
