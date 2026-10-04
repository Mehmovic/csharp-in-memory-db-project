namespace RhinoDB.Core.Tables;

[AttributeUsage(AttributeTargets.Struct, AllowMultiple = true)]
public sealed class PersistentTableAttribute<TDb>() : TableAttribute<TDb>(TableKind.Persistent);
