namespace RhinoDB.Lib.Execution;

public readonly struct DefaultTransaction : ITransaction {
    public int PendingStorageOrphanCount => 0;
    public ulong? LastLsn => null;
    public Result Apply() => Result.Ok();
    public void Discard() { }
    public void SweepDeleted() { }
    public Result ApplyRetainingUndo() => Result.Ok();
    public void ReleaseRetainedUndo() { }
    public bool RevertRetainedUndo() => true;
}
