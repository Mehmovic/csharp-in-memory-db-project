namespace RhinoDB.Core;

public interface IResult<out TSelf> where TSelf : struct, IResult<TSelf> {
    bool IsOk();
    bool IsError();
    bool IsOkOrReverted();

    static abstract TSelf FromError(DbError error);
    static abstract TSelf FromException(Exception ex);
}
