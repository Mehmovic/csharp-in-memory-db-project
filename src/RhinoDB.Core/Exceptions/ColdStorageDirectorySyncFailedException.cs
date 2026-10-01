namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class ColdStorageDirectorySyncFailedException(Exception? inner = null)
    : Exception("Failed to durably persist the cold storage directory after creating its files", inner);
