namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class QueryResultSetDisposedException()
    : Exception("QueryResultSet is disposed");