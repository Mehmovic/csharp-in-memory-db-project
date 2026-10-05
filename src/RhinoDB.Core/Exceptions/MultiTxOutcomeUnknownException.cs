namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class MultiTxOutcomeUnknownException(Exception? inner = null)
    : Exception("A multi-database transaction could not durably record its abort after some participants had already prepared - "
        + "its outcome is decided on the next restart, so the participants refuse further operations until then.", inner);
