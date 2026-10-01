using RhinoDB.Lib.Durability;

namespace RhinoDB.Lib.Changes;

public readonly struct RingEntry(ulong lsn, WalChange change) {
    public ulong Lsn { get; } = lsn;
    public WalChange Change { get; } = change;
}
