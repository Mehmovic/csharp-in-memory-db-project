using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Hashing;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using MemoryPack;
using RhinoDB.Lib.Durability;
using RhinoDB.Lib.Tables;

namespace RhinoDB.Sandbox.Benchmark.Benchmarks;

// What a WAL frame costs NOW that the per-frame UTC timestamp has shipped.
//
// This benchmark answered a decision (2026-09-18): adding a timestamp to the frame was
// measured before it landed - +8 bytes of allocation and frame size, ~20 ns for the clock,
// roughly 0.6% of a 3.4 us operation, and ~6% in the worst (noisiest) paired micro-benchmark.
// That result is recorded in the decision entry; the paired 17-vs-25-byte A/B it came from
// is gone, because there is no longer a 17-byte layout to compare against.
//
// What remains useful is the steady-state cost of the real encode/decode path, and the
// UtcNow call that stamping costs per operation on the single writer thread. The clock
// benchmark repeats per payload size as a control: it should come out flat.
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 3, warmupCount: 8, iterationCount: 12)]
public class WalTimestampBenchmarks {
    private const int FrameCount = 512;
    private const long UtcBase = 638_000_000_000_000_000L;

    [Params(128, 1024)]
    public int PayloadSize { get; set; }

    private byte[] payload = null!;
    private WalChange[] changes = null!;
    private byte[] scanBuffer = null!;
    private long nextLsn;

    [GlobalSetup]
    public void Setup() {
        payload = new byte[PayloadSize];
        BitConverter.TryWriteBytes(payload, 123456789L);

        var row = new byte[100];
        BitConverter.TryWriteBytes(row, 987654321L);
        changes = [new WalChange(12345u, ChangeKind.Update, BitConverter.GetBytes(42), row)];

        scanBuffer = BuildScanBuffer();
    }

    // The stamp costs this much, once per operation, on the writer thread.
    [Benchmark(Baseline = true)]
    public long Clock_UtcNow_Ticks() => DateTime.UtcNow.Ticks;

    [Benchmark]
    public long Clock_Stopwatch_GetTimestamp() => Stopwatch.GetTimestamp();

    // The real encode: MemoryPack the changes, build the 25-byte header, CRC it.
    [Benchmark]
    public int WalCodec_Encode_Stamped() =>
        WalRecordCodec.Encode(nextLsn++, WalEntryKind.Operation, changes, DateTime.UtcNow.Ticks).Length;

    // Exactly the same work with no stamp - the closest remaining A/B, and the one that
    // isolates the timestamp from the rest of the encode.
    [Benchmark]
    public int WalCodec_Encode_Unstamped() =>
        WalRecordCodec.Encode(nextLsn++, WalEntryKind.Operation, changes, WalRecordCodec.Unstamped).Length;

    // The recovery/replay read path over 512 frames.
    [Benchmark]
    public long Scan_512Frames() => Walk(scanBuffer);

    private byte[] BuildScanBuffer() {
        var frames = new List<byte[]>(FrameCount);
        for (var i = 0; i < FrameCount; i++)
            frames.Add(WalRecordCodec.Encode(i + 1, WalEntryKind.Operation, changes, UtcBase + i));

        var total = frames.Sum(f => f.Length);
        var buffer = new byte[total];
        var offset = 0;
        foreach (var frame in frames) {
            frame.CopyTo(buffer.AsSpan(offset));
            offset += frame.Length;
        }
        return buffer;
    }

    // Walks the buffer the way WriteAheadLog.Open does: length, CRC verify, read the header
    // fields, step. Uses the shipped header size rather than a hardcoded 17/25.
    static private long Walk(ReadOnlySpan<byte> buffer) {
        long sum = 0;
        var offset = 0;
        while (offset < buffer.Length) {
            var length = BinaryPrimitives.ReadUInt32LittleEndian(buffer[offset..]);
            var frameSize = WalRecordCodec.HeaderSize + (int)length;
            sum += BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset + 8, 8));
            sum += BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset + 16, 8));
            sum += Crc32.HashToUInt32(buffer.Slice(offset + 8, frameSize - 8));
            offset += frameSize;
        }
        return sum;
    }
}