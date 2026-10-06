namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class PrefsClosedException(Exception? inner = null)
    : Exception("The prefs store is closed - its database's cold storage was disposed.", inner);
