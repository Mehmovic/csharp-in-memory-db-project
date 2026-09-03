namespace RhinoDB.Core.Results;

public sealed partial class RhinoError {
    private readonly Kind kind;
    private readonly object? key;
    private readonly object? attemptedKey;
    private readonly int? offset;
    private readonly Exception? wrapped;

    private RhinoError(Kind kind, object? key = null, object? attemptedKey = null, int? offset = null, Exception? wrapped = null) {
        this.kind = kind;
        this.key = key;
        this.attemptedKey = attemptedKey;
        this.offset = offset;
        this.wrapped = wrapped;
    }

    static public RhinoError Of(Exception? exception) =>
        new RhinoError(Kind.Wrapped, wrapped: exception);

    static public implicit operator RhinoError(Exception? exception) => Of(exception);
}
