namespace RhinoDB.SchemaContracts;

public sealed class RowFieldModel(string fieldName, string fieldTypeFullName, RowFieldKind kind, bool isCustomType) {
    public string FieldName { get; } = fieldName;
    public string FieldTypeFullName { get; } = fieldTypeFullName;
    public RowFieldKind Kind { get; } = kind;
    public bool IsCustomType { get; } = isCustomType;
}
