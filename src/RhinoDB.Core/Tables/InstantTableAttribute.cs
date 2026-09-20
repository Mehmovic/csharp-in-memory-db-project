namespace RhinoDB.Core.Tables;

[AttributeUsage(AttributeTargets.Struct, AllowMultiple = true)]
public sealed class InstantTableAttribute(Type database) : TableAttribute(TableKind.Instant, database) { }
