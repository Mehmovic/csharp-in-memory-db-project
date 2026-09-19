namespace RhinoDB.Lib.Execution;

public interface ITransaction {
    Result Apply();
    void Discard();
    long? LastLsn { get; }
}
