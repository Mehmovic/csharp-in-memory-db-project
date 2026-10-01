namespace RhinoDB.Lib.Tables;

public enum UndoKind : byte {
    Update,
    Delete,
}

public readonly record struct UndoRecord<TRow>(UndoKind Kind, int Offset, TRow Row) where TRow : struct;
