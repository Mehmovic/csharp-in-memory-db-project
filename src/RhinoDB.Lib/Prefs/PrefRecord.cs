using System.Buffers.Binary;

namespace RhinoDB.Lib.Prefs;

// Stored on disk - append only, never renumber.
internal enum PrefKind : byte {
    String = 1,
    Char = 2,
    Bool = 3,
    SByte = 4, Byte = 5, Int16 = 6, UInt16 = 7, Int32 = 8, UInt32 = 9, Int64 = 10, UInt64 = 11, Decimal = 12, 
    Single = 13, Double = 14,
    Bytes = 15,
    Custom = 16,
}

static internal class PrefRecord {
    private const byte Format = 1;
    private const int HeaderLength = 14;

    public const long UseDefault = long.MinValue;
    public const long NeverEvict = -1;

    static public byte[] Encode(PrefKind kind, long cacheTicks, uint typeHash, ReadOnlySpan<byte> payload) {
        var record = new byte[HeaderLength + payload.Length];
        record[0] = Format;
        record[1] = (byte)kind;
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(2), cacheTicks);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(10), typeHash);
        payload.CopyTo(record.AsSpan(HeaderLength));
        return record;
    }

    static public bool TryDecode(byte[] record, out PrefKind kind, out long cacheTicks, out uint typeHash, out ReadOnlySpan<byte> payload) {
        if (record.Length < HeaderLength || record[0] != Format || !Enum.IsDefined((PrefKind)record[1])) {
            kind = default;
            cacheTicks = 0;
            typeHash = 0;
            payload = default;
            return false;
        }
        kind = (PrefKind)record[1];
        cacheTicks = BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(2));
        typeHash = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(10));
        payload = record.AsSpan(HeaderLength);
        return true;
    }

    // null = the default duration, TimeSpan.Zero = never cached, Timeout.InfiniteTimeSpan = never evicted.
    static public bool TryEncodeCacheFor(TimeSpan? cacheFor, out long cacheTicks) {
        cacheTicks = cacheFor switch {
            null => UseDefault,
            { } infinite when infinite == Timeout.InfiniteTimeSpan => NeverEvict,
            { Ticks: >= 0 } duration => duration.Ticks,
            _ => 0,
        };
        return cacheFor is null || cacheFor == Timeout.InfiniteTimeSpan || cacheFor.Value >= TimeSpan.Zero;
    }
}
