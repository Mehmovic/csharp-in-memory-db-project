namespace RhinoDB.Core.Results;

public readonly struct Result<T> {
    private readonly T value;
    private readonly RhinoError? error;

    private readonly bool isError;

    private Result(T value) {
        this.value = value;
        error = null;
        isError = false;
    }

    private Result(RhinoError error) {
        value = default!;
        this.error = error;
        isError = true;
    }

    static public Result<T> Ok(T value) {
        return value is null
            ? throw new ArgumentNullException(nameof(value))
            : new Result<T>(value);
    }

    static public Result<T> Error(RhinoError error) => new Result<T>(error);

    public bool IsOk() => !isError;
    public bool IsError() => isError;

    public RhinoError GetError() {
        return isError ? error!.Value : throw new InvalidOperationException("Result was successful, there is no exception.");
    }

    public void ThrowIfError() {
        if (isError) throw error!.Value.ToException();
    }

    public T Unwrap() {
        return !isError ? value : throw error!.Value.ToException();
    }

    public bool TryUnwrap(out T result) {
        result = !isError ? value : default!;
        return !isError;
    }

    public T UnwrapOr(T orValue) {
        return !isError ? value : orValue;
    }

    public TResult Match<TResult>(Func<T, TResult> onSuccess, Func<RhinoError, TResult> onFailure) =>
        !isError ? onSuccess(value) : onFailure(error!.Value);

    public Result Void() => isError ? Result.Error(error!.Value) : Result.Ok();

    static public implicit operator Result<T>(T value) => Ok(value);

    static public implicit operator Result<T>(Result result) =>
        result.IsOk()
            ? throw new InvalidOperationException("Cannot implicitly convert a successful Result to Result<T> - there is no value to carry.")
            : Error(result.GetError());

    static public implicit operator Result(Result<T> result) => result.Void();
}