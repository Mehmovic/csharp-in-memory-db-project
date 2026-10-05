namespace RhinoDB.Lib.Execution;

public interface ITransaction {
    int PendingStorageOrphanCount { get; }
    ulong? LastLsn { get; }
    Result Apply();
    void Discard();
    void SweepDeleted();
    Result ApplyRetainingUndo();
    void ReleaseRetainedUndo();
    bool RevertRetainedUndo();
}
