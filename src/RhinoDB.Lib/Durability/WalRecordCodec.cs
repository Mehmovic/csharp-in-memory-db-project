using System.Buffers.Binary;
using System.IO.Hashing;
using MemoryPack;

namespace RhinoDB.Lib.Durability;

static public class WalRecordCodec {
    public const int HeaderSize = 4 + 4 + 8 + 1;

    static public byte[] Encode(long lsn, WalEntryKind kind, WalChange[] changes) =>
        BuildFrame(lsn, kind, changes.Length == 0 ? [] : MemoryPackSerializer.Serialize(changes));

    static public byte[] Encode(long lsn, WalEntryKind kind, List<WalChange> changes) =>
        BuildFrame(lsn, kind, changes.Count == 0 ? [] : MemoryPackSerializer.Serialize(changes));

    static private byte[] BuildFrame(long lsn, WalEntryKind kind, byte[] payload) {
        var frame = new byte[HeaderSize + payload.Length];
        var span = frame.AsSpan();

        BinaryPrimitives.WriteUInt32LittleEndian(span[..4], (uint)payload.Length);
        BinaryPrimitives.WriteInt64LittleEndian(span[8..16], lsn);
        span[16] = (byte)kind;
        payload.CopyTo(span[17..]);

        var checksum = Crc32.HashToUInt32(span[8..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..8], checksum);
        return frame;
    }

    static public WalScanStatus TryDecode(ReadOnlySpan<byte> buffer, out DecodedWalEntry entry, out int bytesConsumed) {
        entry = default;
        bytesConsumed = 0;

        if (buffer.Length < HeaderSize) return WalScanStatus.TornTail;

        var length = BinaryPrimitives.ReadUInt32LittleEndian(buffer[..4]);
        var totalFrameSize = HeaderSize + (long)length;
        if (totalFrameSize > buffer.Length) return WalScanStatus.TornTail;

        var frameSize = (int)totalFrameSize;
        var storedChecksum = BinaryPrimitives.ReadUInt32LittleEndian(buffer[4..8]);
        var actualChecksum = Crc32.HashToUInt32(buffer[8..frameSize]);
        if (actualChecksum != storedChecksum) return buffer.Length == frameSize ? WalScanStatus.TornTail : WalScanStatus.Corrupted;

        var lsn = BinaryPrimitives.ReadInt64LittleEndian(buffer[8..16]);
        var kind = (WalEntryKind)buffer[16];
        var payload = buffer[17..frameSize];
        var changes = payload.IsEmpty ? [] : MemoryPackSerializer.Deserialize<WalChange[]>(payload)!;

        entry = new DecodedWalEntry(lsn, kind, changes);
        bytesConsumed = frameSize;
        return WalScanStatus.Clean;
    }

    static public WalScanResult Scan(ReadOnlySpan<byte> buffer) {
        var entries = new List<DecodedWalEntry>();
        var offset = 0;
        while (offset < buffer.Length) {
            var status = TryDecode(buffer[offset..], out var entry, out var consumed);
            if (status != WalScanStatus.Clean) return new WalScanResult(entries, offset, status);
            entries.Add(entry);
            offset += consumed;
        }
        return new WalScanResult(entries, offset, WalScanStatus.Clean);
    }
}
