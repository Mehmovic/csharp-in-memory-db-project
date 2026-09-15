namespace RhinoDB.Lib.Durability;

internal sealed class WalKeyComparer : IEqualityComparer<(uint TableId, byte[] Key)> {
    static public readonly WalKeyComparer Instance = new WalKeyComparer();
    private WalKeyComparer() { }

    public bool Equals((uint TableId, byte[] Key) x, (uint TableId, byte[] Key) y) =>
        x.TableId == y.TableId && x.Key.AsSpan().SequenceEqual(y.Key);

    public int GetHashCode((uint TableId, byte[] Key) obj) {
        var hash = new HashCode();
        hash.Add(obj.TableId);
        hash.AddBytes(obj.Key);
        return hash.ToHashCode();
    }
}