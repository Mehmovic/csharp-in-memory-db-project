namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class ByteOrderUnsupportedException(Exception? inner = null)
    : Exception("RhinoDB stores are little-endian only: libmdbx's files, the row encoding and prefs keep numbers in the machine's native order, and every platform RhinoDB targets is little-endian. This machine is big-endian.", inner);
