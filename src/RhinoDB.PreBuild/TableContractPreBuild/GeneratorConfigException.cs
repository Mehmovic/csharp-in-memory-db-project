namespace RhinoDB.PreBuild;

public sealed class GeneratorConfigException(string message, Exception? innerException = null) : Exception(message, innerException);
