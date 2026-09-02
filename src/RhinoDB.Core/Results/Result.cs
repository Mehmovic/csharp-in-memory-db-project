namespace RhinoDB.Core.Results;

public readonly struct Result {
    private readonly RhinoError? error;

    private readonly bool isError;

    private Result(bool isSuccess, RhinoError? error) {
        isError = !isSuccess;
        this.error = error;
    }

    static public Result Ok() => new Result(true, null);
    static public Result<T> Ok<T>(T val) => Result<T>.Ok(val);

    static public Result Error(RhinoError error) => new Result(false, error);

    public RhinoError GetError() {
        return isError ? error!.Value : throw new InvalidOperationException("Result was successful, there is no exception.");
    }

    public void ThrowIfError() {
        if (isError) throw error!.Value.ToException();
    }

    public bool IsOk() => !isError;
    public bool IsError() => isError;

    public TResult Match<TResult>(Func<TResult> onSuccess, Func<RhinoError, TResult> onFailure) =>
        !isError ? onSuccess() : onFailure(error!.Value);
}
