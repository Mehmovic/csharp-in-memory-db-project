namespace RhinoDB.Lib.Execution;

public interface ITransaction {
    Result Apply();
    void Discard();
    void SweepDeleted();
    int PendingStorageOrphanCount { get; }
    ulong? LastLsn { get; }
}