namespace RhinoDB.Core.Results;

public readonly struct Result {
    private readonly RhinoError? error;

    private Result(RhinoError? error) {
        this.error = error;
    }

    static public Result Ok() => new Result(null);
    static public Result<T> Ok<T>(T val) => Result<T>.Ok(val);

    static public Result Error(RhinoError? error) => new Result(error ?? RhinoError.Of(null));

    public RhinoError GetError() {
        return error ?? throw new InvalidOperationException("Result was successful, there is no exception.");
    }

    public void ThrowIfError() {
        if (error is not null) throw error.ToException();
    }

    public bool IsOk() => error is null;
    public bool IsError() => error is not null;

    public TResult Match<TResult>(Func<TResult> onSuccess, Func<RhinoError, TResult> onFailure) =>
        error is null ? onSuccess() : onFailure(error);
}
