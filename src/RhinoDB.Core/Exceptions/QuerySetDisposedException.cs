namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class QuerySetDisposedException(Exception? inner = null)
    : Exception("QuerySet is disposed", inner);