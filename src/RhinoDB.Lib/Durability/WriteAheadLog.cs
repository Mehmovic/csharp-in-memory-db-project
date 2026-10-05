using Microsoft.Win32.SafeHandles;

using RhinoDB.Native;

namespace RhinoDB.Lib.Durability;

public sealed class WriteAheadLog : IDisposable {
    static private readonly TimeSpan DefaultPeriodicFlushInterval = TimeSpan.FromMilliseconds(100);
    private const long DefaultSizeThresholdBytes = 4 * 1024 * 1024;
    private const int BufferSize = 4096;
    private const ulong CaptureTimestampNow = ulong.MaxValue;

    private readonly FileStream fileStream;
    private readonly SafeFileHandle fileHandle;
    private readonly Timer periodicFlushTimer;
    private readonly long sizeThresholdBytes;
    private readonly Lock appendLock = new Lock();
    private TaskCompletionSource<DbError?>? currentGroup;
    private bool flusherRunning;
    private DbError? failure;
    private long bytesSinceLastFlush;
    private bool disposed;

    #if DEBUG
    internal Action? TestOnlyBeforeFlush { get; set; }
    internal Action? TestOnlyDuringFsync { get; set; }
    #endif
    public Guid DatabaseId { get; }
    public uint Generation { get; }

    private WriteAheadLog(FileStream fileStream, Guid databaseId, uint generation, long sizeThresholdBytes, TimeSpan periodicFlushInterval) {
        this.fileStream = fileStream;
        fileHandle = fileStream.SafeFileHandle;
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

    internal Task<DbError?> AppendConfirmed(ulong lsn, WalEntryKind kind, WalChange[] changes, ulong utcTicks = CaptureTimestampNow, Guid[]? dependsOn = null) =>
        AppendAndJoin(WalRecordCodec.Encode(lsn, kind, changes, Stamp(utcTicks), dependsOn));

    internal Task<DbError?> AppendConfirmed(ulong lsn, WalEntryKind kind, List<WalChange> changes, ulong utcTicks = CaptureTimestampNow, Guid[]? dependsOn = null) =>
        AppendAndJoin(WalRecordCodec.Encode(lsn, kind, changes, Stamp(utcTicks), dependsOn));

    internal void AppendOptimistic(ulong lsn, WalEntryKind kind, WalChange[] changes, ulong utcTicks = CaptureTimestampNow, Guid[]? dependsOn = null) =>
        AppendWithoutWaiting(WalRecordCodec.Encode(lsn, kind, changes, Stamp(utcTicks), dependsOn));

    internal void AppendOptimistic(ulong lsn, WalEntryKind kind, List<WalChange> changes, ulong utcTicks = CaptureTimestampNow, Guid[]? dependsOn = null) =>
        AppendWithoutWaiting(WalRecordCodec.Encode(lsn, kind, changes, Stamp(utcTicks), dependsOn));

    internal Task<DbError?> AppendChainPrepare(ulong lsn, Guid chainId, string[] participants, WalChange[] changes, Guid[] dependsOn) =>
        AppendAndJoin(WalRecordCodec.EncodeChainPrepare(lsn, chainId, participants, changes, (ulong)DateTime.UtcNow.Ticks, dependsOn));

    internal Task<DbError?> AppendChainMarker(WalEntryKind kind, Guid chainId, bool confirmed) {
        var frame = WalRecordCodec.EncodeChainMarker(kind, chainId, (ulong)DateTime.UtcNow.Ticks);
        if (confirmed) return AppendAndJoin(frame);
        AppendWithoutWaiting(frame);
        return Task.FromResult<DbError?>(null);
    }

    private Task<DbError?> AppendAndJoin(byte[] frame) {
        Task<DbError?> durable;
        bool startFlusher;
        lock (appendLock) {
            fileStream.Write(frame, 0, frame.Length);
            bytesSinceLastFlush += frame.Length;
            durable = JoinUnderLock(out startFlusher);
        }
        if (startFlusher) StartFlusher();
        return durable;
    }

    private void AppendWithoutWaiting(byte[] frame) {
        var startFlusher = false;
        lock (appendLock) {
            fileStream.Write(frame, 0, frame.Length);
            bytesSinceLastFlush += frame.Length;
            if (bytesSinceLastFlush >= sizeThresholdBytes) JoinUnderLock(out startFlusher);
        }
        if (startFlusher) StartFlusher();
    }

    internal Task<DbError?> FlushAsync() => RequestFlush();

    private Task<DbError?> RequestFlush() {
        Task<DbError?> durable;
        bool startFlusher;
        lock (appendLock) durable = JoinUnderLock(out startFlusher);
        if (startFlusher) StartFlusher();
        return durable;
    }

    private Task<DbError?> JoinUnderLock(out bool startFlusher) {
        startFlusher = false;
        if (failure is { } failed) return Task.FromResult<DbError?>(failed);
        currentGroup ??= new TaskCompletionSource<DbError?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!flusherRunning) {
            flusherRunning = true;
            startFlusher = true;
        }
        return currentGroup.Task;
    }

    private void StartFlusher() => _ = Task.Run(FlushGroups);

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

    static private ulong Stamp(ulong utcTicks) => utcTicks == CaptureTimestampNow ? (ulong)DateTime.UtcNow.Ticks : utcTicks;

    internal async Task<DbError?> Truncate() {
        var flushError = await RequestFlush();
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

        lock (appendLock) bytesSinceLastFlush = 0;
        return null;
    }

    private void TriggerPeriodicFlushIfPending() {
        try {
            bool pending;
            lock (appendLock) pending = bytesSinceLastFlush > 0;
            if (pending) _ = RequestFlush();
        } catch {
            // An exception escaping a Timer callback terminates the whole process
        }
    }

    private void FlushGroups() {
        while (true) {
            DbError? error = null;
            #if DEBUG
            try { TestOnlyBeforeFlush?.Invoke(); } catch (Exception ex) { error = DbError.WalDurabilityFailed(ex); }
            #endif

            TaskCompletionSource<DbError?>? group;
            lock (appendLock) {
                group = currentGroup;
                currentGroup = null;
                if (group is null) {
                    flusherRunning = false;
                    return;
                }
                if (error is null && failure is null) {
                    try {
                        fileStream.Flush(flushToDisk: false);
                    } catch (Exception ex) {
                        error = DbError.WalDurabilityFailed(ex);
                    }
                }
                bytesSinceLastFlush = 0;
                error ??= failure;
                if (error is { } failed) failure ??= failed;
            }

            if (error is null) {
                try {
                    #if DEBUG
                    TestOnlyDuringFsync?.Invoke();
                    #endif
                    RandomAccess.FlushToDisk(fileHandle);
                } catch (Exception ex) {
                    error = DbError.WalDurabilityFailed(ex);
                    lock (appendLock) failure ??= error;
                }
            }

            group.SetResult(error);
        }
    }

    public void Dispose() {
        if (disposed) return;
        disposed = true;
        periodicFlushTimer.Dispose();

        RequestFlush().GetAwaiter().GetResult();

        lock (appendLock) fileStream.Flush(flushToDisk: true);
        fileStream.Dispose();
    }
}
