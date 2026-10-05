namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class MultiTxParticipantNotDeclaredException(Exception? inner = null)
    : Exception("This database (with this key, for a Child) wasn't declared in LockMultiTx - a locked multi-database transaction holds "
        + "its databases from open on, so every database it touches must be declared up front.", inner);
