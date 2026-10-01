using System.Runtime.InteropServices;

namespace RhinoDB.Core;

[StructLayout(LayoutKind.Auto)]
public readonly partial struct DbError {
    public ErrorKind Kind { get; }
    private readonly Exception? systemException;

    private DbError(ErrorKind kind) {
        Kind = kind;
        systemException = null;
    }

    private DbError(Exception exception) {
        Kind = ErrorKind.SystemFailure;
        systemException = exception ?? new Exception("Something went wrong, no exception captured.");
    }

    private DbError(ErrorKind kind, Exception? exception) {
        Kind = kind;
        systemException = exception;
    }

    static public DbError SystemFailure(Exception exception) => new DbError(exception);
}
