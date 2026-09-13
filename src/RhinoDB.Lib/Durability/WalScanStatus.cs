namespace RhinoDB.Lib.Durability;

public enum WalScanStatus : byte {
    Clean,
    TornTail,
    Corrupted,
}
