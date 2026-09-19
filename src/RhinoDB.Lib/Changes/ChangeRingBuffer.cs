using RhinoDB.Lib.Durability;
using RhinoDB.Lib.Tables;

namespace RhinoDB.Lib.Changes;

public sealed class ChangeRingBuffer(int capacity) {
    private readonly RingEntry[] slots = new RingEntry[capacity];
    private long writePosition;
    private int count;

    public void Record(uint tableId, ChangeKind kind, long lsn, byte[] key, byte[]? row) {
        slots[(int)(writePosition % slots.Length)] = new RingEntry(lsn, new WalChange(tableId, kind, key, row));
        writePosition++;
        if (count < slots.Length) count++;
    }

    public Result<ArrayPoolContainer<RingEntry>> TryGetChangesSince(long lsn) {
        if (count == 0) return ArrayPoolContainer<RingEntry>.Empty();

        var oldestIndex = writePosition - count;
        var oldestLsn = slots[(int)(oldestIndex % slots.Length)].Lsn;
        var newestLsn = slots[(int)((writePosition - 1) % slots.Length)].Lsn;

        if (lsn < oldestLsn - 1) return Result<ArrayPoolContainer<RingEntry>>.Error(DbError.RingBufferGap());
        if (lsn >= newestLsn) return ArrayPoolContainer<RingEntry>.Empty();

        var matchesBuilder = ArrayPoolContainerBuilder<RingEntry>.Create(count);
        for (var i = oldestIndex; i < writePosition; i++) {
            var entry = slots[(int)(i % slots.Length)];
            if (entry.Lsn > lsn) matchesBuilder.Add(entry);
        }

        return matchesBuilder.Build().Unwrap();
    }
}
