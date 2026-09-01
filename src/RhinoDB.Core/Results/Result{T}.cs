namespace RhinoDB.Core.Results;

public readonly struct Result<T> {
    private readonly T value;
    private readonly Exception? exception;

    private readonly bool isError;

    private Result(T value) {
        this.value = value;
        exception = null;
        isError = false;
    }

    private Result(Exception exception) {
        value = default!;
        this.exception = exception;
        isError = true;
    }

    static public Result<T> Ok(T value) {
        return value is null
            ? throw new ArgumentNullException(nameof(value))
            : new Result<T>(value);
    }

    static public Result<T> Error(Exception exception) {
        exception ??= new Exception("Unexpected null exception");
        return new Result<T>(exception);
    }

    public bool IsOk() => !isError;
    public bool IsError() => isError;

    public Exception GetException() {
        return isError ? exception! : throw new InvalidOperationException("Result was successful, there is no exception.");
    }

    public void ThrowIfError() {
        if (isError) throw exception!;
    }

    public T Unwrap() {
        return !isError ? value : throw exception!;
    }

    public bool TryUnwrap(out T result) {
        result = !isError ? value : default!;
        return !isError;
    }

    public T UnwrapOr(T orValue) {
        return !isError ? value : orValue;
    }

    public TResult Match<TResult>(Func<T, TResult> onSuccess, Func<Exception, TResult> onFailure) =>
        !isError ? onSuccess(value) : onFailure(exception!);

    static public implicit operator Result<T>(T value) => Ok(value);

    static public implicit operator Result<T>(Result result) =>
        !result.IsError()
            ? throw new InvalidOperationException("Cannot implicitly convert a successful Result to Result<T> - there is no value to carry.")
            : Error(result.GetException());

    static public implicit operator Result(Result<T> result) =>
        result.IsError() ? Result.Error(result.exception) : Result.Ok();
}
