namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class MultiTxValueNotProducedException(Exception? inner = null)
    : Exception("A TxValue was read before its body ran - bodies only run inside Commit, in Add order, so a value is readable "
        + "by bodies added after the one producing it, or once Commit has succeeded.", inner);
