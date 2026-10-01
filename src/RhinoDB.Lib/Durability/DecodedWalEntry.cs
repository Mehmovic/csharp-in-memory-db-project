namespace RhinoDB.Lib.Durability;

public readonly struct DecodedWalEntry(ulong lsn, WalEntryKind kind, WalChange[] changes, ulong utcTicks = 0) {
    public ulong Lsn { get; } = lsn;
    public WalEntryKind Kind { get; } = kind;
    public WalChange[] Changes { get; } = changes;
    public ulong UtcTicks { get; } = utcTicks;
}