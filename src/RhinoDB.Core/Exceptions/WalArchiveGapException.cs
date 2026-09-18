namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class WalArchiveGapException()
    : Exception("WAL archive history has a gap - a segment is missing between two retained ones");
