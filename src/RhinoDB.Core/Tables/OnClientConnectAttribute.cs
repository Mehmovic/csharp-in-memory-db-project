namespace RhinoDB.Core.Tables;

[AttributeUsage(AttributeTargets.Method)]
public sealed class OnClientConnectAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Method)]
public sealed class OnClientConnectAttribute<TDb> : Attribute { }
