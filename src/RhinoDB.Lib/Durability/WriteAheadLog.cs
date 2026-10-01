using RhinoDB.Native;

namespace RhinoDB.Lib.Durability;

public sealed class WriteAheadLog : IDisposable {
    static private readonly TimeSpan DefaultPeriodicFlushInterval = TimeSpan.FromMilliseconds(100);
    private const long DefaultSizeThresholdBytes = 4 * 1024 * 1024;
    private const int BufferSize = 4096;
    private const long CaptureTimestampNow = -1;

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
            fileStream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, BufferSize, FileOptions.None);
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
            fileStream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, BufferSize, FileOptions.None);
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

    internal Task<DbError?> AppendConfirmed(long lsn, WalEntryKind kind, WalChange[] changes, long utcTicks = CaptureTimestampNow) {
        AppendOnly(lsn, kind, changes, utcTicks);
        return JoinGroupCommit();
    }

    internal Task<DbError?> AppendConfirmed(long lsn, WalEntryKind kind, List<WalChange> changes, long utcTicks = CaptureTimestampNow) {
        AppendOnly(lsn, kind, changes, utcTicks);
        return JoinGroupCommit();
    }

    internal void AppendOptimistic(long lsn, WalEntryKind kind, WalChange[] changes, long utcTicks = CaptureTimestampNow) {
        var frameLength = AppendOnly(lsn, kind, changes, utcTicks);
        if (Interlocked.Add(ref bytesSinceLastFlush, frameLength) >= sizeThresholdBytes) _ = JoinGroupCommit();
    }

    internal void AppendOptimistic(long lsn, WalEntryKind kind, List<WalChange> changes, long utcTicks = CaptureTimestampNow) {
        var frameLength = AppendOnly(lsn, kind, changes, utcTicks);
        if (Interlocked.Add(ref bytesSinceLastFlush, frameLength) >= sizeThresholdBytes) _ = JoinGroupCommit();
    }

    private int AppendOnly(long lsn, WalEntryKind kind, WalChange[] changes, long utcTicks) {
        var stamp = utcTicks < 0 ? DateTime.UtcNow.Ticks : utcTicks;
        var frame = WalRecordCodec.Encode(lsn, kind, changes, stamp);
        lock (appendLock) fileStream.Write(frame, 0, frame.Length);
        return frame.Length;
    }

    private int AppendOnly(long lsn, WalEntryKind kind, List<WalChange> changes, long utcTicks) {
        var stamp = utcTicks < 0 ? DateTime.UtcNow.Ticks : utcTicks;
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
            return DbError.SystemFailure(ex);
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
                error = DbError.SystemFailure(ex);
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
            tcs.SetResult(DbError.SystemFailure(ex));
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
