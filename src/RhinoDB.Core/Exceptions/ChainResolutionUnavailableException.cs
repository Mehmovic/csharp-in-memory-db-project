namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class ChainResolutionUnavailableException(Exception? inner = null)
    : Exception("This store's WAL holds a transaction-chain prepare whose outcome needs the host's chain log to decide - "
        + "refusing to recover rather than guess and truncate. Open it through RhinoHost (or pass a chain resolver).", inner);
