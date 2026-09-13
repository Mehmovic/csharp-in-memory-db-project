namespace RhinoDB.Core;

public interface IResult<out TSelf> where TSelf : struct, IResult<TSelf> {
    bool IsOk();
    static abstract TSelf FromError(DbError error);
    static abstract TSelf FromException(Exception ex);
}
