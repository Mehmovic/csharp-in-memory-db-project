namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class ServerUnavailableException(Exception? inner = null)
    : Exception("The server can't serve this request right now (it is shutting down or restarting) - reconnect and retry.", inner);
