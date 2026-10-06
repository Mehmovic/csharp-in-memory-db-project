namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class PrefTypeNotRegisteredException(Exception? inner = null)
    : Exception("Prefs can store a struct only if it is a [CustomType] - its generated codec registers it.", inner);
