namespace RhinoDB.Core;

public interface IStackResult<out TSelf> where TSelf : struct, IStackResult<TSelf>, allows ref struct {
    bool IsOk();
    bool IsError();
    bool IsOkOrReverted();
    
    static abstract TSelf FromError(DbError error);
    static abstract TSelf FromException(Exception ex);
}
