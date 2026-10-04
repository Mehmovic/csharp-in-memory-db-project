namespace RhinoDB.Core.Tables;

[AttributeUsage(AttributeTargets.Method)]
public sealed class OnClientDisconnectAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Method)]
public sealed class OnClientDisconnectAttribute<TDb> : Attribute { }
