using System.Buffers.Binary;

namespace RhinoDB.Lib.Durability;

static public class WalFileHeaderCodec {
    public const int Size = 4 + 16;
    public const uint CurrentVersion = 1;

    static public byte[] Encode(Guid databaseId) {
        var buffer = new byte[Size];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, CurrentVersion);
        databaseId.TryWriteBytes(buffer.AsSpan(4));
        return buffer;
    }

    static public bool TryDecode(ReadOnlySpan<byte> buffer, out WalFileHeader header) {
        header = default;
        if (buffer.Length < Size) return false;

        var version = BinaryPrimitives.ReadUInt32LittleEndian(buffer[..4]);
        var databaseId = new Guid(buffer.Slice(4, 16));
        header = new WalFileHeader(version, databaseId);
        return version == CurrentVersion;
    }
}
