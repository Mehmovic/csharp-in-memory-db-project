using System.Buffers.Binary;
using System.IO.Hashing;
using MemoryPack;

namespace RhinoDB.Lib.Durability;

[MemoryPackable]
internal readonly partial struct WalChainPreparePayload(Guid chainId, string[] participants, WalChange[] changes) {
    public Guid ChainId { get; } = chainId;
    public string[] Participants { get; } = participants;
    public WalChange[] Changes { get; } = changes;
}

static public class WalRecordCodec {
    public const int HeaderSize = 4 + 4 + 8 + 8 + 1;
    public const long Unstamped = 0;
    private const int ChainIdSize = 16;


    static public byte[] Encode(ulong lsn, WalEntryKind kind, WalChange[] changes, ulong utcTicks = Unstamped) =>
        BuildFrame(lsn, kind, changes.Length == 0 ? [] : MemoryPackSerializer.Serialize(changes), utcTicks);

    static public byte[] Encode(ulong lsn, WalEntryKind kind, List<WalChange> changes, ulong utcTicks = Unstamped) =>
        BuildFrame(lsn, kind, changes.Count == 0 ? [] : MemoryPackSerializer.Serialize(changes), utcTicks);

    static public byte[] EncodeChainPrepare(ulong lsn, Guid chainId, string[] participants, WalChange[] changes, ulong utcTicks = Unstamped) =>
        BuildFrame(lsn, WalEntryKind.ChainPrepare,
            MemoryPackSerializer.Serialize(new WalChainPreparePayload(chainId, participants, changes)), utcTicks);

    static public byte[] EncodeChainMarker(WalEntryKind kind, Guid chainId, ulong utcTicks = Unstamped) {
        if (kind is not (WalEntryKind.ChainCommit or WalEntryKind.ChainAbort))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only ChainCommit/ChainAbort are chain markers.");
        return BuildFrame(0, kind, chainId.ToByteArray(), utcTicks);
    }

    static private byte[] BuildFrame(ulong lsn, WalEntryKind kind, byte[] payload, ulong utcTicks) {
        var frame = new byte[HeaderSize + payload.Length];
        var span = frame.AsSpan();

        BinaryPrimitives.WriteUInt32LittleEndian(span[..4], (uint)payload.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(span[8..16], lsn);
        BinaryPrimitives.WriteUInt64LittleEndian(span[16..24], utcTicks);
        span[24] = (byte)kind;
        payload.CopyTo(span[25..]);

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

        var lsn = BinaryPrimitives.ReadUInt64LittleEndian(buffer[8..16]);
        var utcTicks = BinaryPrimitives.ReadUInt64LittleEndian(buffer[16..24]);
        var kind = (WalEntryKind)buffer[24];
        var payload = buffer[25..frameSize];

        switch (kind) {
            case WalEntryKind.ChainPrepare: {
                var prepare = MemoryPackSerializer.Deserialize<WalChainPreparePayload>(payload);
                entry = new DecodedWalEntry(lsn, kind, prepare.Changes ?? [], utcTicks, prepare.ChainId, prepare.Participants);
                break;
            }
            case WalEntryKind.ChainCommit or WalEntryKind.ChainAbort: {
                if (payload.Length != ChainIdSize) return WalScanStatus.Corrupted;
                entry = new DecodedWalEntry(lsn, kind, [], utcTicks, new Guid(payload));
                break;
            }
            default: {
                var changes = payload.IsEmpty ? [] : MemoryPackSerializer.Deserialize<WalChange[]>(payload)!;
                entry = new DecodedWalEntry(lsn, kind, changes, utcTicks);
                break;
            }
        }

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
