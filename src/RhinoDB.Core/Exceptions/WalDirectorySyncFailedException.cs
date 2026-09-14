namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class WalDirectorySyncFailedException()
    : Exception("Failed to durably persist the WAL directory after creating its file");
