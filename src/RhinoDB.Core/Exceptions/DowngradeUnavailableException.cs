namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class DowngradeUnavailableException()
    : Exception("Refusing to downgrade this row for the client - no registered [ClientDowngrade] chain "
        + "bridges the server's current revision down to what the client expects. The client must upgrade "
        + "its app to receive this data.");
