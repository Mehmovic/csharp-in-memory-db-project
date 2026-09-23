namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class QuerySetDisposedException()
    : Exception("QuerySet is disposed");