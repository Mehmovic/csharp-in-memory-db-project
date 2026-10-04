namespace RhinoDB.Core.Tables;

[AttributeUsage(AttributeTargets.Class)]
public sealed class ChildDatabaseAttribute<TRoot, TKey> : Attribute { }
