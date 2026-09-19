using RhinoDB.Lib.Durability;

namespace RhinoDB.Lib.Changes;

public readonly struct RingEntry(long lsn, WalChange change) {
    public long Lsn { get; } = lsn;
    public WalChange Change { get; } = change;
}
