namespace RhinoDB.Lib.Durability;

public readonly struct DecodedWalEntry(long lsn, WalEntryKind kind, WalChange[] changes, long utcTicks = 0) {
    public long Lsn { get; } = lsn;
    public WalEntryKind Kind { get; } = kind;
    public WalChange[] Changes { get; } = changes;
    public long UtcTicks { get; } = utcTicks;
}