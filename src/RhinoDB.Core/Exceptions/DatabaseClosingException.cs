namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class DatabaseClosingException(Exception? inner = null)
    : Exception("This database is draining in-flight work before closing - it no longer accepts new operations.", inner);
