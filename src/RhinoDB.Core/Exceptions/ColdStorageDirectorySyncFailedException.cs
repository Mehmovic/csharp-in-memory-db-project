namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class ColdStorageDirectorySyncFailedException()
    : Exception("Failed to durably persist the cold storage directory after creating its files");
