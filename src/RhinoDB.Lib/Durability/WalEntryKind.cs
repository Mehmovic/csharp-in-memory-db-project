namespace RhinoDB.Lib.Durability;

public enum WalEntryKind : byte {
    Operation = 0,
    CheckpointMarker = 1,
    ChainPrepare = 2,
    ChainCommit = 3,
    ChainAbort = 4,
}
