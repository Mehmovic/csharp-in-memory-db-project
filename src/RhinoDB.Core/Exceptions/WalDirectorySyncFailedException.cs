namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class WalDirectorySyncFailedException(Exception? inner = null)
    : Exception("Failed to durably persist the WAL directory after creating its file", inner);
