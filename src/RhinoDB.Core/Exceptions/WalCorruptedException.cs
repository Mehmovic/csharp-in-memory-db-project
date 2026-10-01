namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class WalCorruptedException(Exception? inner = null)
    : Exception("WAL record checksum mismatch mid-file - not a torn tail, refusing to open", inner);
