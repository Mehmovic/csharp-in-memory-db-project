namespace RhinoDB.Lib.Execution;

public interface ITransaction {
    Result Apply();
}
