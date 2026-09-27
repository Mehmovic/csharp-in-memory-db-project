namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class ClientAppVersionInvalidException()
    : Exception("Refusing this client - its app version is declared invalid via "
        + "[InvalidClientAppVersions], meaning it must upgrade before any request can be served.");
