namespace RhinoDB.Core.Tables;

[AttributeUsage(AttributeTargets.Method)]
public sealed class OnInitAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Method)]
public sealed class OnInitAttribute<TDb> : Attribute { }
