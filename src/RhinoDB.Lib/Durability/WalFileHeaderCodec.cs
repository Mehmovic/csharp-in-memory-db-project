using System.Buffers.Binary;

namespace RhinoDB.Lib.Durability;

// File header at position 0, Docs/05-wal-design.md Phase 1: format version + database
// identity. A mismatch on either refuses to open rather than guessing - the WAL for
// database A must never be silently replayed against database B's in-memory schema.
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
