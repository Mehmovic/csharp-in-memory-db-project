namespace RhinoDB.Core.Tables;

[AttributeUsage(AttributeTargets.Struct, AllowMultiple = true)]
public class TableAttribute(TableKind kind, Type database) : Attribute {
    public TableKind Kind { get; } = kind;
    public Type Database { get; } = database;
    public string? Accessor { get; set; }
    public int ChunkSize { get; set; } = 4096;
    public bool Evictable { get; set; }
    public int RingBufferCapacity { get; set; }
}
