using System.Buffers.Binary;
using System.IO.Hashing;
using MemoryPack;

namespace RhinoDB.Lib.Durability;

// Entry layout, Docs/05-wal-design.md Phase 1, amended for the operation-level LSN
// resolution (one entry per operation, not per change):
// [u32 length][u32 checksum][u64 lsn][byte kind][payload]
// checksum covers everything from lsn through the end of payload - never the length
// or checksum fields themselves. `payload` is MemoryPack-encoded WalChange[] for
// Operation entries, empty for CheckpointMarker entries (the lsn alone is the watermark).
static public class WalRecordCodec {
    public const int HeaderSize = 4 + 4 + 8 + 1;

    static public byte[] Encode(long lsn, WalEntryKind kind, WalChange[] changes) {
        var payload = changes.Length == 0 ? [] : MemoryPackSerializer.Serialize(changes);
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

    // A checksum mismatch is only ever reported as TornTail when this record sits
    // exactly at the end of the supplied buffer (nothing follows it) - a genuine
    // crash-mid-append leaves a trailing partial/garbled frame with nothing after it.
    // A checksum mismatch with further bytes trailing it means real corruption struck
    // an already-fsync'd record, which should never happen and is treated as fatal.
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
