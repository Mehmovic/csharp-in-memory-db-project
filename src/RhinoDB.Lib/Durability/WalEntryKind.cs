namespace RhinoDB.Lib.Durability;

public enum WalEntryKind : byte {
    Operation = 0,
    CheckpointMarker = 1,
}
