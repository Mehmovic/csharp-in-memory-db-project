namespace RhinoDB.Core.Rpc;

[AttributeUsage(AttributeTargets.Method)]
public sealed class RpcCommandAttribute : Attribute {
    public string? Name { get; set; }
}
