namespace RhinoDB.Core.Tables;

[AttributeUsage(AttributeTargets.Struct, AllowMultiple = true)]
public sealed class PersistentTableAttribute(Type database) : TableAttribute(TableKind.Persistent, database) { }
