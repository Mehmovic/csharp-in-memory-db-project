namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class PrefKeyInvalidException(Exception? inner = null)
    : Exception("A prefs key must be a non-empty string of at most 255 UTF-8 bytes.", inner);
