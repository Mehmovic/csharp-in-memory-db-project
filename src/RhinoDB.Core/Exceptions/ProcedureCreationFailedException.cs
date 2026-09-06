namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class ProcedureCreationFailedException() : Exception("Creating procedure on execution loop failed.");
