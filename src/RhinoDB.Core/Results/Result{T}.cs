namespace RhinoDB.Core;

public readonly struct Result<T> {
    private readonly T value;
    private readonly DbError? error;

    private Result(T value) {
        this.value = value;
        error = null;
    }

    private Result(DbError error) {
        value = default!;
        this.error = error;
    }

    static public Result<T> Ok(T value) {
        return value is null
            ? throw new ArgumentNullException(nameof(value))
            : new Result<T>(value);
    }

    static public Result<T> Error(DbError error) => new Result<T>(error);

    public bool IsOk() => error is null;
    public bool IsError() => error is not null;

    public DbError GetError() {
        return error ?? throw new InvalidOperationException("Result was successful, there is no exception.");
    }

    public void ThrowIfError() {
        if (error is not null) throw error.Value.ToException();
    }

    public T Unwrap() {
        return error is null ? value : throw error.Value.ToException();
    }

    public bool TryUnwrap(out T result) {
        result = error is null ? value : default!;
        return error is null;
    }

    public T UnwrapOr(T orValue) {
        return error is null ? value : orValue;
    }

    public TResult Match<TResult>(Func<T, TResult> onSuccess, Func<DbError, TResult> onFailure) =>
        error is null ? onSuccess(value) : onFailure(error.Value);

    public Result Void() => error is null ? Result.Ok() : Result.Error(error.Value);

    static public implicit operator Result<T>(T value) => Ok(value);

    static public implicit operator Result<T>(Result result) =>
        result.IsOk()
            ? throw new InvalidOperationException("Cannot implicitly convert a successful Result to Result<T> - there is no value to carry.")
            : Error(result.GetError());

    static public implicit operator Result(Result<T> result) => result.Void();
}
