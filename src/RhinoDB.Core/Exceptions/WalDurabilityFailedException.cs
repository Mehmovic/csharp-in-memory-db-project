namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class WalDurabilityFailedException()
    : Exception("WAL fsync failed - the durability gate is broken, the database refuses further operations until restarted");