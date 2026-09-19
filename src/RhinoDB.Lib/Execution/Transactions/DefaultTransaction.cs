namespace RhinoDB.Lib.Execution;

public readonly struct DefaultTransaction : ITransaction {
    public Result Apply() => Result.Ok();
    public void Discard() { }
    public long? LastLsn => null;
}
