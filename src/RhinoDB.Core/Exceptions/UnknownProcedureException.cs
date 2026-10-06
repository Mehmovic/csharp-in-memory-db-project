namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class UnknownProcedureException(Exception? inner = null)
    : Exception("Unknown procedure - the client is likely built against a different server revision, or the procedure hash matches nothing registered on this host.", inner);
