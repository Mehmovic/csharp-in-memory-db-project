namespace RhinoDB.Lib.Durability;

public sealed class EvictionBatch(long sizeThresholdBytes = 4 * 1024 * 1024) {
    private readonly List<EvictionCandidate> staged = [];
    private readonly Lock stagingLock = new Lock();
    private long stagedBytes;

    public bool Stage(uint tableId, byte[] key) {
        lock (stagingLock) {
            staged.Add(new EvictionCandidate(tableId, key));
            stagedBytes += key.Length;
            return stagedBytes >= sizeThresholdBytes;
        }
    }

    public IReadOnlyList<EvictionCandidate> DrainStaged() {
        lock (stagingLock) {
            var drained = staged.ToArray();
            staged.Clear();
            stagedBytes = 0;
            return drained;
        }
    }
}
