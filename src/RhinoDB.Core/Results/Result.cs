namespace RhinoDB.Core;

public readonly struct Result : IResult<Result> {
    private readonly DbError? error;

    private Result(DbError? error) {
        this.error = error;
    }

    static public Result Ok() => new Result(null);
    static public Result<T> Ok<T>(T val) => Result<T>.Ok(val);

    static public Result Error(DbError error) => new Result(error);
    static public Result Error(Exception ex) => new Result(DbError.SystemFailure(ex));

    static Result IResult<Result>.FromError(DbError error) => Error(error);
    static Result IResult<Result>.FromException(Exception ex) => Error(ex);

    public DbError GetError() {
        return error ?? throw new InvalidOperationException("Result was successful, there is no exception.");
    }

    public void ThrowIfError() {
        if (error is not null) throw error.Value.ToException();
    }

    public bool IsOk() => error is null;
    public bool IsError() => error is not null;

    public TResult Match<TResult>(Func<TResult> onSuccess, Func<DbError, TResult> onFailure) =>
        error is null ? onSuccess() : onFailure(error.Value);
}
