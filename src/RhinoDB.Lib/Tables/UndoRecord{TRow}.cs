namespace RhinoDB.Lib.Tables;

public readonly record struct UndoRecord<TRow>(int Offset, TRow Row) where TRow : struct;
