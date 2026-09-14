namespace RhinoDB.Lib.Durability;

public sealed class EvictionBatch(long sizeThresholdBytes = EvictionBatch.DefaultSizeThresholdBytes) {
    public const long DefaultSizeThresholdBytes = 4 * 1024 * 1024;

    private readonly List<EvictionCandidate> staged = [];
    private long stagedBytes;

    public bool Stage(uint tableId, byte[] key) {
        staged.Add(new EvictionCandidate(tableId, key));
        stagedBytes += key.Length;
        return stagedBytes >= sizeThresholdBytes;
    }

    public void DrainStaged(List<EvictionCandidate> into) {
        into.Clear();
        into.AddRange(staged);
        staged.Clear();
        stagedBytes = 0;
    }
}
