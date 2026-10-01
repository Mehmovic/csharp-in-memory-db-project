namespace RhinoDB.Lib.Execution;

public readonly struct DefaultTransaction : ITransaction {
    public Result Apply() => Result.Ok();
    public void Discard() { }
    public void SweepDeleted() { }
    public int PendingStorageOrphanCount => 0;
    public long? LastLsn => null;
}