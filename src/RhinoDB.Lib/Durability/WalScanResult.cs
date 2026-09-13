namespace RhinoDB.Lib.Durability;

public readonly struct WalScanResult(List<DecodedWalEntry> entries, int validLength, WalScanStatus status) {
    public List<DecodedWalEntry> Entries { get; } = entries;
    public int ValidLength { get; } = validLength;
    public WalScanStatus Status { get; } = status;
}
