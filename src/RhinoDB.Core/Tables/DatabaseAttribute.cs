namespace RhinoDB.Core.Tables;

[AttributeUsage(AttributeTargets.Class)]
public sealed class DatabaseAttribute : Attribute {
    public int RingBufferCapacity { get; set; } = 10_000;
}
