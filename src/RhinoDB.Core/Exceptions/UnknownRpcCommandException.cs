namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class UnknownRpcCommandException(Exception? inner = null)
    : Exception("Unknown RPC command - the client is likely built against a different server revision "
        + "than this one, or the command hash doesn't match anything registered on this host.", inner);
