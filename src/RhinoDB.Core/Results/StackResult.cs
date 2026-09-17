namespace RhinoDB.Core;

public readonly ref struct StackResult : IStackResult<StackResult> {
    private readonly DbError? error;

    private StackResult(DbError? error) {
        this.error = error;
    }

    static public StackResult Ok() => new StackResult(null);
    static public StackResult<T> Ok<T>(T val) where T: allows ref struct
        => StackResult<T>.Ok(val);

    static public StackResult Error(DbError error) => new StackResult(error);
    static public StackResult Error(Exception ex) => new StackResult(DbError.SystemFailure(ex));

    static StackResult IStackResult<StackResult>.FromError(DbError error) => Error(error);
    static StackResult IStackResult<StackResult>.FromException(Exception ex) => Error(ex);

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
