namespace RhinoDB.Core.Results;

public readonly struct Result {
    private readonly Exception? exception;

    private readonly bool isError;

    private Result(bool isSuccess, Exception? exception) {
        isError = !isSuccess;
        this.exception = exception;
    }

    static public Result Ok() => new Result(true, null);
    
    static public Result Error(Exception exception) {
        exception ??= new Exception("Unexpected null exception");
        return new Result(false, exception);
    }

    public Exception GetException() {
        return isError ? exception! : throw new InvalidOperationException("Result was successful, there is no exception.");
    }

    public void ThrowIfError() {
        if (isError) throw exception!;
    }
    
    public bool IsOk() => !isError;
    public bool IsError() => isError;

    public TResult Match<TResult>(Func<TResult> onSuccess, Func<Exception, TResult> onFailure) =>
        !isError ? onSuccess() : onFailure(exception!);
}
