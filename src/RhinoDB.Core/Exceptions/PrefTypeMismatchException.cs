namespace RhinoDB.Core.Exceptions;

[GenerateDbError]
public sealed class PrefTypeMismatchException(Exception? inner = null)
    : Exception("The prefs key holds a value of a different type than the one requested.", inner);
