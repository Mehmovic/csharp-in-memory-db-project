namespace RhinoDB.Core.Tables;

public abstract class TableAttributeBase(TableKind kind) : Attribute {
    public TableKind Kind { get; } = kind;
    public string? Accessor { get; set; }
    public int ChunkSize { get; set; } = 4096;
    public bool Evictable { get; set; }
    public int RingBufferCapacity { get; set; }
}

[AttributeUsage(AttributeTargets.Struct, AllowMultiple = true)]
public sealed class TableAttribute(TableKind kind) : TableAttributeBase(kind);

[AttributeUsage(AttributeTargets.Struct, AllowMultiple = true)]
public class TableAttribute<TDb>(TableKind kind) : TableAttributeBase(kind);
