namespace RhinoDB.Lib.Cold;

public sealed class MdbxNativeException(int code, string message) : Exception(message) {
    public int Code { get; } = code;
}
