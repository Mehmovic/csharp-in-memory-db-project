using BenchmarkDotNet.Attributes;

namespace RhinoDB.Sandbox.Benchmark.Benchmarks;

[MemoryDiagnoser]
public class WalPrototypeBenchmarks {
    private const int ConcurrentWriters = 16;
    private const int BatchSize = 10_000;
    private const int PayloadSize = 64;
    private const long SettledKey = 1;

    private string filePath = null!;
    private FileStream fileStream = null!;
    private readonly Lock appendLock = new Lock();
    private readonly Lock groupLock = new Lock();
    private TaskCompletionSource<bool>? inFlightGroup;
    private long nextFreshKey;

    [GlobalSetup]
    public void Setup() {
        var dir = Path.Combine(Path.GetTempPath(), "rhinodb-bench");
        Directory.CreateDirectory(dir);
        filePath = Path.Combine(dir, $"wal-prototype-{Guid.NewGuid():N}.dat");
        fileStream = new FileStream(filePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.None);

        AppendOnly(BuildEntry(0, SettledKey));
        fileStream.Flush(flushToDisk: true);
        nextFreshKey = 1_000_000;
    }

    [GlobalCleanup]
    public void Cleanup() {
        fileStream.Dispose();
        File.Delete(filePath);
    }

    [Benchmark]
    public Task SingleConfirmedSettledKeyUpdate() => AppendConfirmed(BuildEntry(0, SettledKey));

    [Benchmark]
    public Task BatchFreshKeyConfirmedInserts() {
        var tasks = new Task[BatchSize];
        for (var i = 0; i < BatchSize; i++) tasks[i] = AppendConfirmed(BuildEntry(0, Interlocked.Increment(ref nextFreshKey)));
        return Task.WhenAll(tasks);
    }

    [Benchmark]
    public Task ConcurrentConfirmedFreshKeyInserts() {
        var tasks = new Task[ConcurrentWriters];
        for (var i = 0; i < ConcurrentWriters; i++) tasks[i] = AppendConfirmed(BuildEntry(0, Interlocked.Increment(ref nextFreshKey)));
        return Task.WhenAll(tasks);
    }

    [Benchmark]
    public Task ConcurrentConfirmedSettledKeyUpdates() {
        var tasks = new Task[ConcurrentWriters];
        for (var i = 0; i < ConcurrentWriters; i++) tasks[i] = AppendConfirmed(BuildEntry(0, SettledKey));
        return Task.WhenAll(tasks);
    }

    // "Confirmed" call: append (fast, in-memory, sequential) then join/trigger a group fsync -
    // pure piggyback-on-in-flight, no Task.Delay/window anywhere (Docs/05-wal-design.md Phase 2).
    private Task AppendConfirmed(byte[] entry) {
        AppendOnly(entry);
        return JoinGroupCommit();
    }

    private void AppendOnly(byte[] entry) {
        lock (appendLock) fileStream.Write(entry, 0, entry.Length);
    }

    private Task JoinGroupCommit() {
        lock (groupLock) {
            if (inFlightGroup is { } existing) return existing.Task;

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            inFlightGroup = tcs;
            _ = Task.Run(() => {
                fileStream.Flush(flushToDisk: true);
                lock (groupLock) { if (ReferenceEquals(inFlightGroup, tcs)) inFlightGroup = null; }
                tcs.SetResult(true);
            });
            return tcs.Task;
        }
    }

    // [u32 length][u32 checksum][u64 lsn][u64 utcTicks][byte kind][payload] - Docs/05-wal-design.md Phase 1's
    // entry layout, close enough for a throwaway prototype to include the real per-entry cost
    // (checksum computation, header packing) alongside the I/O it exists to measure.
    static private byte[] BuildEntry(ulong lsn, long key) {
        var payload = new byte[PayloadSize];
        BitConverter.TryWriteBytes(payload, key);

        var entry = new byte[4 + 4 + 8 + 8 + 1 + payload.Length];
        var span = entry.AsSpan();
        BitConverter.TryWriteBytes(span[..4], payload.Length);
        BitConverter.TryWriteBytes(span[8..16], lsn);
        BitConverter.TryWriteBytes(span[16..24], DateTime.UtcNow.Ticks);
        span[24] = 1;
        payload.CopyTo(span[25..]);
        BitConverter.TryWriteBytes(span[4..8], Fnv1A(span[8..]));
        return entry;
    }

    static private uint Fnv1A(ReadOnlySpan<byte> data) {
        var hash = 2166136261u;
        foreach (var b in data) hash = (hash ^ b) * 16777619u;
        return hash;
    }
}
