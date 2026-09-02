using RhinoDB.Core.Exceptions;

namespace RhinoDB.Core.Results;

public readonly struct RhinoError {
    private enum Kind { DuplicateKey, IndexKeyNotFound, OffsetNotRegistered, PrimaryKeyImmutable, Wrapped }

    private readonly Kind kind;
    private readonly object? key;
    private readonly object? attemptedKey;
    private readonly int offset;
    private readonly Exception? wrapped;

    private RhinoError(Kind kind, object? key = null, object? attemptedKey = null, int offset = 0, Exception? wrapped = null) {
        this.kind = kind;
        this.key = key;
        this.attemptedKey = attemptedKey;
        this.offset = offset;
        this.wrapped = wrapped;
    }

    public Exception ToException() => kind switch {
        Kind.DuplicateKey => new DuplicateKeyException(key!),
        Kind.IndexKeyNotFound => new IndexKeyNotFoundException(key!),
        Kind.OffsetNotRegistered => new OffsetNotRegisteredException(key!, offset),
        Kind.PrimaryKeyImmutable => new PrimaryKeyImmutableException(key!, attemptedKey!),
        _ => wrapped ?? new Exception("Unexpected error, empty exception caught")
    };

    static public RhinoError Of(Exception? exception) => new(Kind.Wrapped, wrapped: exception);

    static public RhinoError DuplicateKey(object key) => new(Kind.DuplicateKey, key: key);

    static public RhinoError IndexKeyNotFound(object key) => new(Kind.IndexKeyNotFound, key: key);

    static public RhinoError OffsetNotRegistered(object key, int offset) =>
        new(Kind.OffsetNotRegistered, key: key, offset: offset);

    static public RhinoError PrimaryKeyImmutable(object key, object attemptedKey) =>
        new(Kind.PrimaryKeyImmutable, key: key, attemptedKey: attemptedKey);

    static public implicit operator RhinoError(Exception? exception) => Of(exception);
}
