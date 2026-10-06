using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

using RhinoDB.Core.CustomTypes;
using RhinoDB.Lib.Cold;
using RhinoDB.Native;

namespace RhinoDB.Lib.Prefs;

// Small durable key/value store for server-side state that isn't game data - like Unity's PlayerPrefs, but on the
// server: one per process (the Root database's store), reached as host.Prefs or ctx.Prefs.
public sealed class RhinoPrefs {
    private const string TableName = "__rhino_prefs";
    private const int MaxKeyBytes = 255;
    private const uint CreateTableFlag = 0x40000;
    private const uint ReadOnlyTxn = 0x20000;
    private const int MdbxNotFound = -30798;

    private readonly ColdStore cold;
    private readonly TimeProvider clock;
    private readonly ConcurrentDictionary<string, CacheEntry> cache = new ConcurrentDictionary<string, CacheEntry>(StringComparer.Ordinal);
    private readonly SemaphoreSlim writeGate = new SemaphoreSlim(1, 1);
    private readonly ReaderWriterLockSlim useLock = new ReaderWriterLockSlim();
    private readonly ITimer sweeper;
    private long writeGeneration;
    private bool closed;

    public TimeSpan DefaultCacheDuration { get; }

    internal RhinoPrefs(ColdStore cold, TimeSpan defaultCacheDuration, TimeProvider? clock = null) {
        this.cold = cold;
        this.clock = clock ?? TimeProvider.System;
        DefaultCacheDuration = defaultCacheDuration;
        var sweepEvery = TimeSpan.FromTicks(Math.Clamp(defaultCacheDuration.Ticks / 4, TimeSpan.TicksPerSecond, TimeSpan.TicksPerMinute));
        sweeper = this.clock.CreateTimer(static state => ((RhinoPrefs)state!).EvictExpired(), this, sweepEvery, sweepEvery);
    }

    // ---- reads ----

    public Result<string> GetString(string key, string defaultValue = "") =>
        Read(key, PrefKind.String, 0, defaultValue, static value => (string)value);

    public Result<byte> GetByte(string key, byte defaultValue = 0) =>
        Read(key, PrefKind.Byte, 0, defaultValue, static value => (byte)value);

    public Result<sbyte> GetSByte(string key, sbyte defaultValue = 0) =>
        Read(key, PrefKind.SByte, 0, defaultValue, static value => (sbyte)value);

    public Result<short> GetInt16(string key, short defaultValue = 0) =>
        Read(key, PrefKind.Int16, 0, defaultValue, static value => (short)value);

    public Result<ushort> GetUInt16(string key, ushort defaultValue = 0) =>
        Read(key, PrefKind.UInt16, 0, defaultValue, static value => (ushort)value);

    public Result<int> GetInt32(string key, int defaultValue = 0) =>
        Read(key, PrefKind.Int32, 0, defaultValue, static value => (int)value);

    public Result<uint> GetUInt32(string key, uint defaultValue = 0) =>
        Read(key, PrefKind.UInt32, 0, defaultValue, static value => (uint)value);

    public Result<long> GetInt64(string key, long defaultValue = 0) =>
        Read(key, PrefKind.Int64, 0, defaultValue, static value => (long)value);

    public Result<ulong> GetUInt64(string key, ulong defaultValue = 0) =>
        Read(key, PrefKind.UInt64, 0, defaultValue, static value => (ulong)value);

    public Result<float> GetSingle(string key, float defaultValue = 0) =>
        Read(key, PrefKind.Single, 0, defaultValue, static value => (float)value);

    public Result<double> GetDouble(string key, double defaultValue = 0) =>
        Read(key, PrefKind.Double, 0, defaultValue, static value => (double)value);

    public Result<decimal> GetDecimal(string key, decimal defaultValue = 0) =>
        Read(key, PrefKind.Decimal, 0, defaultValue, static value => (decimal)value);

    public Result<char> GetChar(string key, char defaultValue = '\0') =>
        Read(key, PrefKind.Char, 0, defaultValue, static value => (char)value);

    public Result<bool> GetBool(string key, bool defaultValue = false) =>
        Read(key, PrefKind.Bool, 0, defaultValue, static value => (bool)value);

    // A copy - changing it never changes the stored or cached value.
    public Result<byte[]> GetBytes(string key) =>
        Read(key, PrefKind.Bytes, 0, [], static value => ((byte[])value).ToArray());

    // The same stored value as GetBytes - a byte[] and a List<byte> are interchangeable.
    public Result<List<byte>> GetByteList(string key) =>
        Read(key, PrefKind.Bytes, 0, [], static value => new List<byte>((byte[])value));

    // T must be a [CustomType]: its generated codec registers it when its assembly loads.
    public Result<T> Get<T>(string key, T defaultValue = default) where T : struct =>
        !CustomTypeCodec<T>.IsRegistered
            ? Result<T>.Error(DbError.PrefTypeNotRegistered())
            : Read(key, PrefKind.Custom, CustomTypeCodec<T>.TypeHash, defaultValue, static value => CustomTypeCodec<T>.Deserialize((byte[])value));

    public Result<bool> HasKey(string key) {
        if (!IsValidKey(key)) return Result<bool>.Error(DbError.PrefKeyInvalid());
        var loaded = Load(key);
        return loaded.IsError() ? Result<bool>.Error(loaded.GetError()) : !ReferenceEquals(loaded.Unwrap(), CacheEntry.Missing);
    }

    // Every stored key, in ordinal byte order.
    public Result<IReadOnlyList<string>> Keys() {
        useLock.EnterReadLock();
        try {
            if (closed) return Result<IReadOnlyList<string>>.Error(DbError.PrefsClosed());
            var rc = cold.Environment.BeginTxn(ReadOnlyTxn, out var txn);
            if (rc != 0 || txn is null) return Result<IReadOnlyList<string>>.Error(MdbxErrorMapper.Map(rc));
            using var scope = txn;
            rc = txn.OpenCursor(cold.PrefsTable, out var cursor);
            if (rc != 0 || cursor is null) return Result<IReadOnlyList<string>>.Error(MdbxErrorMapper.Map(rc));
            using var cursorScope = cursor;

            var keys = new List<string>();
            for (rc = cursor.GetFirst(out var key, out _); rc == 0; rc = cursor.GetNext(out key, out _)) keys.Add(Encoding.UTF8.GetString(key));
            return rc == MdbxNotFound ? keys : Result<IReadOnlyList<string>>.Error(MdbxErrorMapper.Map(rc));
        } finally {
            useLock.ExitReadLock();
        }
    }

    // ---- writes: each is on disk when its task completes ----

    public Task<Result> SetStringAsync(string key, string value, TimeSpan? cacheFor = null) => WriteAsync([SetString(key, value, cacheFor)]);

    public Task<Result> SetByteAsync(string key, byte value, TimeSpan? cacheFor = null) => WriteAsync([SetValue(key, PrefKind.Byte, value, cacheFor)]);

    public Task<Result> SetSByteAsync(string key, sbyte value, TimeSpan? cacheFor = null) => WriteAsync([SetValue(key, PrefKind.SByte, value, cacheFor)]);

    public Task<Result> SetInt16Async(string key, short value, TimeSpan? cacheFor = null) => WriteAsync([SetValue(key, PrefKind.Int16, value, cacheFor)]);

    public Task<Result> SetUInt16Async(string key, ushort value, TimeSpan? cacheFor = null) => WriteAsync([SetValue(key, PrefKind.UInt16, value, cacheFor)]);

    public Task<Result> SetInt32Async(string key, int value, TimeSpan? cacheFor = null) => WriteAsync([SetValue(key, PrefKind.Int32, value, cacheFor)]);

    public Task<Result> SetUInt32Async(string key, uint value, TimeSpan? cacheFor = null) => WriteAsync([SetValue(key, PrefKind.UInt32, value, cacheFor)]);

    public Task<Result> SetInt64Async(string key, long value, TimeSpan? cacheFor = null) => WriteAsync([SetValue(key, PrefKind.Int64, value, cacheFor)]);

    public Task<Result> SetUInt64Async(string key, ulong value, TimeSpan? cacheFor = null) => WriteAsync([SetValue(key, PrefKind.UInt64, value, cacheFor)]);

    public Task<Result> SetSingleAsync(string key, float value, TimeSpan? cacheFor = null) => WriteAsync([SetValue(key, PrefKind.Single, value, cacheFor)]);

    public Task<Result> SetDoubleAsync(string key, double value, TimeSpan? cacheFor = null) => WriteAsync([SetValue(key, PrefKind.Double, value, cacheFor)]);

    public Task<Result> SetDecimalAsync(string key, decimal value, TimeSpan? cacheFor = null) => WriteAsync([SetValue(key, PrefKind.Decimal, value, cacheFor)]);

    public Task<Result> SetCharAsync(string key, char value, TimeSpan? cacheFor = null) => WriteAsync([SetValue(key, PrefKind.Char, value, cacheFor)]);

    public Task<Result> SetBoolAsync(string key, bool value, TimeSpan? cacheFor = null) => WriteAsync([SetValue(key, PrefKind.Bool, value, cacheFor)]);

    public Task<Result> SetBytesAsync(string key, byte[] value, TimeSpan? cacheFor = null) => WriteAsync([SetBytes(key, value, cacheFor)]);

    public Task<Result> SetByteListAsync(string key, List<byte> value, TimeSpan? cacheFor = null) => WriteAsync([SetByteList(key, value, cacheFor)]);

    public Task<Result> SetAsync<T>(string key, T value, TimeSpan? cacheFor = null) where T : struct => WriteAsync([Set(key, value, cacheFor)]);

    public Task<Result> DeleteAsync(string key) => WriteAsync([Delete(key)]);

    public Task<Result> DeleteAllAsync() => WriteAsync([PrefOp.ClearAll]);

    // Several writes, one transaction, one fsync: all of them land or none does.
    public RhinoPrefsBatch Batch() => new RhinoPrefsBatch(this);

    // ---- operations (shared with RhinoPrefsBatch); a rejected one carries its error and fails the whole write ----

    static internal PrefOp SetString(string key, string value, TimeSpan? cacheFor) =>
        value is null ? PrefOp.Rejected(DbError.SystemFailure(new ArgumentNullException(nameof(value))))
            : PrefOp.Put(key, PrefKind.String, 0, Encoding.UTF8.GetBytes(value), value, cacheFor);

    // Numbers, char and bool: their little-endian bytes (every platform .NET runs RhinoDB on is little-endian).
    static internal PrefOp SetValue<T>(string key, PrefKind kind, T value, TimeSpan? cacheFor) where T : unmanaged =>
        PrefOp.Put(key, kind, 0, MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value)).ToArray(), value, cacheFor);

    static internal PrefOp SetBytes(string key, byte[] value, TimeSpan? cacheFor) {
        if (value is null) return PrefOp.Rejected(DbError.SystemFailure(new ArgumentNullException(nameof(value))));
        var copy = value.ToArray();
        return PrefOp.Put(key, PrefKind.Bytes, 0, copy, copy, cacheFor);
    }

    static internal PrefOp SetByteList(string key, List<byte> value, TimeSpan? cacheFor) {
        if (value is null) return PrefOp.Rejected(DbError.SystemFailure(new ArgumentNullException(nameof(value))));
        var copy = CollectionsMarshal.AsSpan(value).ToArray();
        return PrefOp.Put(key, PrefKind.Bytes, 0, copy, copy, cacheFor);
    }

    static internal PrefOp Set<T>(string key, T value, TimeSpan? cacheFor) where T : struct {
        if (!CustomTypeCodec<T>.IsRegistered) return PrefOp.Rejected(DbError.PrefTypeNotRegistered());
        byte[] payload;
        try {
            payload = CustomTypeCodec<T>.Serialize(value);
        } catch (Exception ex) {
            return PrefOp.Rejected(DbError.SystemFailure(ex));
        }
        return PrefOp.Put(key, PrefKind.Custom, CustomTypeCodec<T>.TypeHash, payload, payload, cacheFor);
    }

    static internal PrefOp Delete(string key) =>
        IsValidKey(key) ? PrefOp.Remove(key) : PrefOp.Rejected(DbError.PrefKeyInvalid());

    internal async Task<Result> WriteAsync(IReadOnlyList<PrefOp> ops) {
        foreach (var op in ops)
            if (op.Error is { } rejected) return Result.Error(rejected);
        if (ops.Count == 0) return Result.Ok();

        await writeGate.WaitAsync().ConfigureAwait(false);
        try {
            // libmdbx binds a write transaction to the thread that began it - begin, commit and fsync in one synchronous run.
            return await Task.Run(() => Write(ops)).ConfigureAwait(false);
        } finally {
            writeGate.Release();
        }
    }

    private Result Write(IReadOnlyList<PrefOp> ops) {
        useLock.EnterReadLock();
        try {
            if (closed) return Result.Error(DbError.PrefsClosed());
            var rc = cold.Environment.BeginTxn(0, out var txn);
            if (rc != 0 || txn is null) return Result.Error(MdbxErrorMapper.Map(rc));
            using (txn) {
                var changed = false;
                foreach (var op in ops) {
                    // A not-found delete marks a libmdbx transaction failed - look first, delete only what exists.
                    if (op.Kind == PrefOpKind.Remove) {
                        rc = txn.Get(cold.PrefsTable, op.KeyBytes, out _);
                        if (rc == MdbxNotFound) continue;
                        if (rc != 0) return Result.Error(MdbxErrorMapper.Map(rc));
                    }
                    rc = op.Kind switch {
                        PrefOpKind.Put => txn.Put(cold.PrefsTable, op.KeyBytes, op.Record!, 0),
                        PrefOpKind.Remove => txn.Delete(cold.PrefsTable, op.KeyBytes),
                        _ => txn.Drop(cold.PrefsTable, del: false),
                    };
                    if (rc != 0) return Result.Error(MdbxErrorMapper.Map(rc));
                    changed = true;
                }
                if (!changed) return Result.Ok();
                rc = txn.Commit();
                if (rc != 0) return Result.Error(MdbxErrorMapper.Map(rc));
            }

            // Committed, so visible to readers - the cache follows even if the fsync below fails.
            Interlocked.Increment(ref writeGeneration);
            var now = Now;
            foreach (var op in ops) {
                switch (op.Kind) {
                    case PrefOpKind.Put:
                        var entry = new CacheEntry(op.ValueKind, op.TypeHash, op.Value!, ResolveCacheTicks(op.CacheTicks), now);
                        if (entry.CacheTicks == 0) cache.TryRemove(op.Key, out _);
                        else cache[op.Key] = entry;
                        break;
                    case PrefOpKind.Remove:
                        cache.TryRemove(op.Key, out _);
                        break;
                    default:
                        cache.Clear();
                        break;
                }
            }

            rc = cold.Environment.Sync(force: true, nonblock: false);
            return rc == 0 ? Result.Ok() : Result.Error(MdbxErrorMapper.Map(rc));
        } finally {
            useLock.ExitReadLock();
        }
    }

    // ---- cache ----

    internal int CachedCount => cache.Count;

    internal bool IsCached(string key) => cache.ContainsKey(key);

    internal int EvictExpired() {
        var now = Now;
        var evicted = 0;
        foreach (var pair in cache) {
            var entry = pair.Value;
            if (entry.CacheTicks <= 0 || now - Volatile.Read(ref entry.LastAccessTicks) < entry.CacheTicks) continue;
            if (cache.TryRemove(pair)) evicted++;
        }
        return evicted;
    }

    private Result<T> Read<T>(string key, PrefKind kind, uint typeHash, T defaultValue, Func<object, T> convert) {
        if (!IsValidKey(key)) return Result<T>.Error(DbError.PrefKeyInvalid());
        var loaded = Load(key);
        if (loaded.IsError()) return Result<T>.Error(loaded.GetError());
        var entry = loaded.Unwrap();
        if (ReferenceEquals(entry, CacheEntry.Missing)) return defaultValue is null ? Result<T>.Error(DbError.SystemFailure(new ArgumentNullException(nameof(defaultValue)))) : Result<T>.Ok(defaultValue);
        if (entry.Kind != kind || entry.TypeHash != typeHash) return Result<T>.Error(DbError.PrefTypeMismatch());
        try {
            return Result<T>.Ok(convert(entry.Value));
        } catch (Exception ex) {
            return Result<T>.Error(DbError.SystemFailure(ex));
        }
    }

    // CacheEntry.Missing when the key isn't stored. Loads from disk on a miss and caches the value unless a write raced the load.
    private Result<CacheEntry> Load(string key) {
        if (cache.TryGetValue(key, out var cached)) {
            Volatile.Write(ref cached.LastAccessTicks, Now);
            return cached;
        }

        useLock.EnterReadLock();
        try {
            if (closed) return Result<CacheEntry>.Error(DbError.PrefsClosed());
            var generation = Interlocked.Read(ref writeGeneration);
            var rc = cold.Environment.BeginTxn(ReadOnlyTxn, out var txn);
            if (rc != 0 || txn is null) return Result<CacheEntry>.Error(MdbxErrorMapper.Map(rc));
            byte[] record;
            using (txn) {
                rc = txn.Get(cold.PrefsTable, Encoding.UTF8.GetBytes(key), out record);
            }
            if (rc == MdbxNotFound) return CacheEntry.Missing;
            if (rc != 0) return Result<CacheEntry>.Error(MdbxErrorMapper.Map(rc));

            if (!PrefRecord.TryDecode(record, out var kind, out var cacheTicks, out var typeHash, out var payload))
                return Result<CacheEntry>.Error(DbError.SystemFailure(new InvalidDataException($"Prefs value for '{key}' is not a valid record.")));
            if (DecodeValue(kind, payload) is not { } value)
                return Result<CacheEntry>.Error(DbError.SystemFailure(new InvalidDataException($"Prefs value for '{key}' doesn't match its type.")));
            var entry = new CacheEntry(kind, typeHash, value, ResolveCacheTicks(cacheTicks), Now);

            // A write that committed after our read already updated the cache - don't put the older value back.
            if (entry.CacheTicks != 0 && Interlocked.Read(ref writeGeneration) == generation) cache.TryAdd(key, entry);
            return entry;
        } finally {
            useLock.ExitReadLock();
        }
    }

    // Null when the payload doesn't fit its kind (a damaged record).
    static private object? DecodeValue(PrefKind kind, ReadOnlySpan<byte> payload) => kind switch {
        PrefKind.String => Encoding.UTF8.GetString(payload),
        PrefKind.Bytes or PrefKind.Custom => payload.ToArray(),
        PrefKind.Byte => ReadValue<byte>(payload),
        PrefKind.SByte => ReadValue<sbyte>(payload),
        PrefKind.Int16 => ReadValue<short>(payload),
        PrefKind.UInt16 => ReadValue<ushort>(payload),
        PrefKind.Int32 => ReadValue<int>(payload),
        PrefKind.UInt32 => ReadValue<uint>(payload),
        PrefKind.Int64 => ReadValue<long>(payload),
        PrefKind.UInt64 => ReadValue<ulong>(payload),
        PrefKind.Single => ReadValue<float>(payload),
        PrefKind.Double => ReadValue<double>(payload),
        PrefKind.Decimal => ReadValue<decimal>(payload),
        PrefKind.Char => ReadValue<char>(payload),
        PrefKind.Bool => payload.Length == 1 ? payload[0] != 0 : null,
        _ => null,
    };

    static private object? ReadValue<T>(ReadOnlySpan<byte> payload) where T : unmanaged =>
        payload.Length == Unsafe.SizeOf<T>() ? MemoryMarshal.Read<T>(payload) : null;

    private long ResolveCacheTicks(long stored) => stored == PrefRecord.UseDefault ? DefaultCacheDuration.Ticks : stored;

    private long Now => clock.GetUtcNow().UtcTicks;

    static internal bool IsValidKey(string? key) =>
        !string.IsNullOrEmpty(key) && Encoding.UTF8.GetByteCount(key) <= MaxKeyBytes;

    // ---- lifetime ----

    // Waits for an in-flight write, then refuses everything - called by ColdStore.Dispose before libmdbx closes.
    internal void Close() {
        useLock.EnterWriteLock();
        try {
            closed = true;
            sweeper.Dispose();
            cache.Clear();
        } finally {
            useLock.ExitWriteLock();
        }
    }

    static internal Result<uint> CreateTable(MdbxEnvironment env) {
        var rc = env.BeginTxn(0, out var txn);
        if (rc != 0 || txn is null) return Result<uint>.Error(MdbxErrorMapper.Map(rc));
        using (txn) {
            rc = txn.OpenDbi(TableName, CreateTableFlag, out var dbi);
            if (rc != 0) return Result<uint>.Error(MdbxErrorMapper.Map(rc));
            rc = txn.Commit();
            return rc != 0 ? Result<uint>.Error(MdbxErrorMapper.Map(rc)) : dbi;
        }
    }

    private sealed class CacheEntry(PrefKind kind, uint typeHash, object value, long cacheTicks, long lastAccessTicks) {
        static public readonly CacheEntry Missing = new CacheEntry(0, 0, new object(), 0, 0);

        public readonly PrefKind Kind = kind;
        public readonly uint TypeHash = typeHash;
        public readonly object Value = value;
        public readonly long CacheTicks = cacheTicks;
        public long LastAccessTicks = lastAccessTicks;
    }
}

internal enum PrefOpKind : byte { Put, Remove, ClearAll }

internal sealed class PrefOp {
    static public readonly PrefOp ClearAll = new PrefOp { Kind = PrefOpKind.ClearAll };

    public PrefOpKind Kind { get; private init; }
    public string Key { get; private init; } = "";
    public byte[] KeyBytes { get; private init; } = [];
    public byte[]? Record { get; private init; }
    public PrefKind ValueKind { get; private init; }
    public uint TypeHash { get; private init; }
    public object? Value { get; private init; }
    public long CacheTicks { get; private init; }
    public DbError? Error { get; private init; }

    static public PrefOp Put(string key, PrefKind kind, uint typeHash, byte[] payload, object value, TimeSpan? cacheFor) {
        if (!RhinoPrefs.IsValidKey(key)) return Rejected(DbError.PrefKeyInvalid());
        if (!PrefRecord.TryEncodeCacheFor(cacheFor, out var cacheTicks)) return Rejected(DbError.PrefCacheDurationInvalid());
        return new PrefOp {
            Kind = PrefOpKind.Put,
            Key = key,
            KeyBytes = Encoding.UTF8.GetBytes(key),
            Record = PrefRecord.Encode(kind, cacheTicks, typeHash, payload),
            ValueKind = kind,
            TypeHash = typeHash,
            Value = value,
            CacheTicks = cacheTicks,
        };
    }

    static public PrefOp Remove(string key) => new PrefOp { Kind = PrefOpKind.Remove, Key = key, KeyBytes = Encoding.UTF8.GetBytes(key) };

    static public PrefOp Rejected(DbError error) => new PrefOp { Error = error };
}
