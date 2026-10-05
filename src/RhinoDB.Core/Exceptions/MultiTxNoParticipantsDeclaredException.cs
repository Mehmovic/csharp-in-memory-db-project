namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class MultiTxNoParticipantsDeclaredException(Exception? inner = null)
    : Exception("LockMultiTx declared no databases - declare every database the transaction will touch.", inner);
