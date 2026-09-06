namespace RhinoDB.Core;

public readonly partial struct DbError {
    public ErrorKind Kind { get; }

    private DbError(ErrorKind kind) {
        Kind = kind;
    }
}
