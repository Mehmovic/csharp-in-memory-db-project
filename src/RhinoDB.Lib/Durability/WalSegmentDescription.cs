namespace RhinoDB.Lib.Durability;

public readonly record struct WalSegmentDescription(
    string Path,
    uint Generation,
    int EntryCount,
    long OldestUtcTicks,
    long NewestUtcTicks,
    bool HasAnyUnstampedEntry) {
    public string FileName => System.IO.Path.GetFileName(Path);

    public bool IsWhollyOlderThan(long cutoffUtcTicks) {
        if (EntryCount == 0) return true;
        if (HasAnyUnstampedEntry) return false;
        return NewestUtcTicks < cutoffUtcTicks;
    }
}