namespace RhinoDB.Core;

public readonly ref struct StackResult<T> : IStackResult<StackResult<T>> where T: allows ref struct {
    private readonly T? value;
    private readonly DbError? error;

    private StackResult(T value) {
        this.value = value;
        error = null;
    }

    private StackResult(DbError error) {
        value = default;
        this.error = error;
    }

    static public StackResult<T> Ok(T value) {
        return value is null
            ? throw new ArgumentNullException(nameof(value))
            : new StackResult<T>(value);
    }

    static public StackResult<T> Error(DbError error) => new StackResult<T>(error);
    static public StackResult<T> Error(Exception ex) => new StackResult<T>(DbError.SystemFailure(ex));

    static StackResult<T> IStackResult<StackResult<T>>.FromError(DbError error) => Error(error);
    static StackResult<T> IStackResult<StackResult<T>>.FromException(Exception ex) => Error(ex);

    public bool IsOk() => error is null;
    public bool IsError() => error is not null;
    public bool IsOkOrReverted() => error is null || error.Value.Kind == ErrorKind.ApplyFailedButRevertedSuccessfully;

    public DbError GetError() {
        return error ?? throw new InvalidOperationException("Result was successful, there is no exception.");
    }

    public void ThrowIfError() {
        if (error is not null) throw error.Value.ToException();
    }

    public T Unwrap() {
        return error is null ? value! : throw error.Value.ToException();
    }

    public bool TryUnwrap(out T result) {
        result = error is null ? value! : default!;
        return error is null;
    }

    public T UnwrapOr(T orValue) {
        return error is null ? value! : orValue;
    }

    public TResult Match<TResult>(Func<T, TResult> onSuccess, Func<DbError, TResult> onFailure) =>
        error is null ? onSuccess(value!) : onFailure(error.Value);

    public StackResult Void() => error is null ? StackResult.Ok() : StackResult.Error(error.Value);

    static public implicit operator StackResult<T>(T value) => Ok(value);

    static public implicit operator StackResult<T>(Result result) =>
        result.IsOk()
            ? throw new InvalidOperationException("Cannot implicitly convert a successful Result to Result<T> - there is no value to carry.")
            : Error(result.GetError());
    
    static public implicit operator StackResult<T>(StackResult result) =>
        result.IsOk()
            ? throw new InvalidOperationException("Cannot implicitly convert a successful Result to Result<T> - there is no value to carry.")
            : Error(result.GetError());

    static public implicit operator StackResult(StackResult<T> result) => result.Void();
}
