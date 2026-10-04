namespace RhinoDB.Core.Tables;

[AttributeUsage(AttributeTargets.Struct, AllowMultiple = true)]
public sealed class InstantTableAttribute<TDb>() : TableAttribute<TDb>(TableKind.Instant);
