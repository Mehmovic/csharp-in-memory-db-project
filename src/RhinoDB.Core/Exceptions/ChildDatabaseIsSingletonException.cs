namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class ChildDatabaseIsSingletonException(Exception? inner = null)
    : Exception("This child database is a singleton ([ChildDatabase<TRoot>]) - it has no key and is never disposed; "
        + "reach it through its keyless short forms.", inner);
