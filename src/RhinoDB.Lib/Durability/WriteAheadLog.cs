using RhinoDB.Native;

namespace RhinoDB.Lib.Durability;

public sealed class WriteAheadLog : IDisposable {
    static private readonly TimeSpan DefaultPeriodicFlushInterval = TimeSpan.FromMilliseconds(100);
    private const long DefaultSizeThresholdBytes = 4 * 1024 * 1024;
    private const int BufferSize = 4096;
    private const ulong CaptureTimestampNow = ulong.MaxValue;

    private readonly FileStream fileStream;
    private readonly Timer periodicFlushTimer;
    private readonly long sizeThresholdBytes;
    private readonly Lock appendLock = new Lock();
    private readonly Lock groupLock = new Lock();
    private TaskCompletionSource<DbError?>? inFlightGroup;
    private long bytesSinceLastFlush;
    private bool disposed;

    #if DEBUG
    internal Action? TestOnlyBeforeFlush { get; set; }
    #endif
    public Guid DatabaseId { get; }
    public uint Generation { get; }

    private WriteAheadLog(FileStream fileStream, Guid databaseId, uint generation, long sizeThresholdBytes, TimeSpan periodicFlushInterval) {
        this.fileStream = fileStream;
        DatabaseId = databaseId;
        Generation = generation;
        this.sizeThresholdBytes = sizeThresholdBytes;
        periodicFlushTimer = new Timer(_ => TriggerPeriodicFlushIfPending(), null, periodicFlushInterval, periodicFlushInterval);
    }

    static public Result<WriteAheadLog> Create(
        string path, Guid databaseId, uint generation, long sizeThresholdBytes = DefaultSizeThresholdBytes, TimeSpan periodicFlushInterval = default) {
        if (periodicFlushInterval == TimeSpan.Zero) periodicFlushInterval = DefaultPeriodicFlushInterval;

        FileStream fileStream;
        try {
            fileStream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, BufferSize, FileOptions.None);
        } catch (IOException ex) {
            return Result<WriteAheadLog>.Error(DbError.SystemFailure(ex));
        }

        var header = WalFileHeaderCodec.Encode(databaseId, generation);
        fileStream.Write(header, 0, header.Length);
        fileStream.Flush(flushToDisk: true);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && !DirectorySync.TrySync(directory, out _)) {
            fileStream.Dispose();
            return Result<WriteAheadLog>.Error(DbError.WalDirectorySyncFailed());
        }

        return Result<WriteAheadLog>.Ok(new WriteAheadLog(fileStream, databaseId, generation, sizeThresholdBytes, periodicFlushInterval));
    }

    static public Result<(WriteAheadLog Wal, DecodedWalEntry[] Entries)> Open(
        string path, long sizeThresholdBytes = DefaultSizeThresholdBytes, TimeSpan periodicFlushInterval = default) {
        if (periodicFlushInterval == TimeSpan.Zero) periodicFlushInterval = DefaultPeriodicFlushInterval;

        FileStream fileStream;
        try {
            fileStream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, BufferSize, FileOptions.None);
        } catch (IOException ex) {
            return Result<(WriteAheadLog, DecodedWalEntry[])>.Error(DbError.SystemFailure(ex));
        }

        var headerBytes = new byte[WalFileHeaderCodec.Size];
        var headerRead = fileStream.Read(headerBytes, 0, headerBytes.Length);
        if (headerRead < headerBytes.Length || !WalFileHeaderCodec.TryDecode(headerBytes, out var header)) {
            fileStream.Dispose();
            return Result<(WriteAheadLog, DecodedWalEntry[])>.Error(DbError.WalCorrupted());
        }

        var tailLength = (int)(fileStream.Length - WalFileHeaderCodec.Size);
        var tailBytes = new byte[tailLength];
        fileStream.ReadExactly(tailBytes, 0, tailLength);

        var scan = WalRecordCodec.Scan(tailBytes);
        if (scan.Status == WalScanStatus.Corrupted) {
            fileStream.Dispose();
            return Result<(WriteAheadLog, DecodedWalEntry[])>.Error(DbError.WalCorrupted());
        }

        if (scan.Status == WalScanStatus.TornTail) fileStream.SetLength(WalFileHeaderCodec.Size + scan.ValidLength);
        fileStream.Seek(0, SeekOrigin.End);

        return Result<(WriteAheadLog, DecodedWalEntry[])>.Ok(
            (new WriteAheadLog(fileStream, header.DatabaseId, header.Generation, sizeThresholdBytes, periodicFlushInterval), scan.Entries.ToArray()));
    }

    internal Task<DbError?> AppendConfirmed(ulong lsn, WalEntryKind kind, WalChange[] changes, ulong utcTicks = CaptureTimestampNow) {
        AppendOnly(lsn, kind, changes, utcTicks);
        return JoinGroupCommit();
    }

    internal Task<DbError?> AppendConfirmed(ulong lsn, WalEntryKind kind, List<WalChange> changes, ulong utcTicks = CaptureTimestampNow) {
        AppendOnly(lsn, kind, changes, utcTicks);
        return JoinGroupCommit();
    }

    internal void AppendOptimistic(ulong lsn, WalEntryKind kind, WalChange[] changes, ulong utcTicks = CaptureTimestampNow) {
        var frameLength = AppendOnly(lsn, kind, changes, utcTicks);
        if (Interlocked.Add(ref bytesSinceLastFlush, frameLength) >= sizeThresholdBytes) _ = JoinGroupCommit();
    }

    internal void AppendOptimistic(ulong lsn, WalEntryKind kind, List<WalChange> changes, ulong utcTicks = CaptureTimestampNow) {
        var frameLength = AppendOnly(lsn, kind, changes, utcTicks);
        if (Interlocked.Add(ref bytesSinceLastFlush, frameLength) >= sizeThresholdBytes) _ = JoinGroupCommit();
    }

    internal Task<DbError?> AppendChainPrepareConfirmed(ulong lsn, Guid chainId, string[] participants, WalChange[] changes) {
        AppendFrame(WalRecordCodec.EncodeChainPrepare(lsn, chainId, participants, changes, (ulong)DateTime.UtcNow.Ticks));
        return JoinGroupCommit();
    }

    internal Task<DbError?> AppendChainMarker(WalEntryKind kind, Guid chainId, bool confirmed) {
        var frameLength = AppendFrame(WalRecordCodec.EncodeChainMarker(kind, chainId, (ulong)DateTime.UtcNow.Ticks));
        if (confirmed) return JoinGroupCommit();
        if (Interlocked.Add(ref bytesSinceLastFlush, frameLength) >= sizeThresholdBytes) _ = JoinGroupCommit();
        return Task.FromResult<DbError?>(null);
    }

    private int AppendFrame(byte[] frame) {
        lock (appendLock) fileStream.Write(frame, 0, frame.Length);
        return frame.Length;
    }

    static public Result<DecodedWalEntry[]> ReadEntriesShared(string path) {
        if (!File.Exists(path)) return Result<DecodedWalEntry[]>.Ok([]);
        try {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes, 0, bytes.Length);
            if (bytes.Length < WalFileHeaderCodec.Size || !WalFileHeaderCodec.TryDecode(bytes, out _))
                return Result<DecodedWalEntry[]>.Error(DbError.WalCorrupted());

            var scan = WalRecordCodec.Scan(bytes.AsSpan(WalFileHeaderCodec.Size));
            return scan.Status == WalScanStatus.Corrupted
                ? Result<DecodedWalEntry[]>.Error(DbError.WalCorrupted())
                : Result<DecodedWalEntry[]>.Ok(scan.Entries.ToArray());
        } catch (IOException ex) {
            return Result<DecodedWalEntry[]>.Error(DbError.SystemFailure(ex));
        }
    }

    private int AppendOnly(ulong lsn, WalEntryKind kind, WalChange[] changes, ulong utcTicks) {
        var stamp = utcTicks == CaptureTimestampNow ? (ulong)DateTime.UtcNow.Ticks : utcTicks;
        var frame = WalRecordCodec.Encode(lsn, kind, changes, stamp);
        lock (appendLock) fileStream.Write(frame, 0, frame.Length);
        return frame.Length;
    }

    private int AppendOnly(ulong lsn, WalEntryKind kind, List<WalChange> changes, ulong utcTicks) {
        var stamp = utcTicks == CaptureTimestampNow ? (ulong)DateTime.UtcNow.Ticks : utcTicks;
        var frame = WalRecordCodec.Encode(lsn, kind, changes, stamp);
        lock (appendLock) fileStream.Write(frame, 0, frame.Length);
        return frame.Length;
    }

    internal async Task<DbError?> Truncate() {
        var flushError = await JoinGroupCommit();
        if (flushError is { } err) return err;

        try {
            lock (appendLock) {
                fileStream.SetLength(WalFileHeaderCodec.Size);
                fileStream.Seek(WalFileHeaderCodec.Size, SeekOrigin.Begin);
                fileStream.Flush(flushToDisk: true);
            }
        } catch (Exception ex) {
            return DbError.WalDurabilityFailed(ex);
        }

        Interlocked.Exchange(ref bytesSinceLastFlush, 0);
        return null;
    }

    private void TriggerPeriodicFlushIfPending() {
        try {
            if (Volatile.Read(ref bytesSinceLastFlush) > 0)
                _ = JoinGroupCommit();
        } catch {
            // An exception escaping a Timer callback terminates the whole process
        }
    }

    private Task<DbError?> JoinGroupCommit() {
        lock (groupLock) {
            if (inFlightGroup is { } existing) return existing.Task;

            var tcs = new TaskCompletionSource<DbError?>(TaskCreationOptions.RunContinuationsAsynchronously);
            inFlightGroup = tcs;
            _ = Task.Run(() => RunFlush(tcs));
            return tcs.Task;
        }
    }

    private void RunFlush(TaskCompletionSource<DbError?> tcs) {
    #if DEBUG
        if (TestOnlyBeforeFlushInvocationFailed(tcs)) return;
    #endif
        
        DbError? error = null;

        lock (appendLock) {
            try {
                fileStream.Flush(flushToDisk: true);
                Interlocked.Exchange(ref bytesSinceLastFlush, 0);
            } catch (Exception ex) {
                error = DbError.WalDurabilityFailed(ex);
            }

            lock (groupLock) { if (ReferenceEquals(inFlightGroup, tcs)) inFlightGroup = null; }
        }

        tcs.SetResult(error);
    }

    #if DEBUG
    private bool TestOnlyBeforeFlushInvocationFailed(TaskCompletionSource<DbError?> tcs) {
        try {
            TestOnlyBeforeFlush?.Invoke();
            return false;
        } catch (Exception ex) {
            lock (appendLock) {
                lock (groupLock) { if (ReferenceEquals(inFlightGroup, tcs)) inFlightGroup = null; }
            }
            tcs.SetResult(DbError.WalDurabilityFailed(ex));
            return true;
        }
    }
    #endif

    public void Dispose() {
        if (disposed) return;
        disposed = true;
        periodicFlushTimer.Dispose();

        Task<DbError?>? pending;
        lock (groupLock) pending = inFlightGroup?.Task;
        pending?.GetAwaiter().GetResult();

        lock (appendLock) fileStream.Flush(flushToDisk: true);
        fileStream.Dispose();
    }
}
