namespace RhinoDB.Lib.Durability;

public readonly struct DecodedWalEntry(long lsn, WalEntryKind kind, WalChange[] changes) {
    public long Lsn { get; } = lsn;
    public WalEntryKind Kind { get; } = kind;
    public WalChange[] Changes { get; } = changes;
}
