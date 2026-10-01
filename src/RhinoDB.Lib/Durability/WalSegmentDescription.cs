namespace RhinoDB.Lib.Durability;

public readonly record struct WalSegmentDescription(
    string Path,
    uint Generation,
    int EntryCount,
    ulong OldestUtcTicks,
    ulong NewestUtcTicks,
    bool HasAnyUnstampedEntry) {
    public string FileName => System.IO.Path.GetFileName(Path);

    public bool IsWhollyOlderThan(ulong cutoffUtcTicks) {
        if (EntryCount == 0) return true;
        if (HasAnyUnstampedEntry) return false;
        return NewestUtcTicks < cutoffUtcTicks;
    }
}