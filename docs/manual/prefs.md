# Prefs: a durable key/value store for the server

`RhinoPrefs` is the server's equivalent of Unity's `PlayerPrefs`: a small store for values
the server itself writes and wants back after a restart. Good uses:

- the current season, a maintenance flag, a message of the day;
- the last time a daily job ran;
- feature switches flipped at runtime.

There is exactly one per process. It lives in the Root database's storage, in a reserved
libmdbx table next to your tables.

```csharp
await host.Prefs.SetInt64Async("season.current", 7);       // on disk when the task completes
var season = host.Prefs.GetInt64("season.current", 1);     // Result<long>: 1 if never set
```

## When to use it, and when not

Prefs are **not** game data. They sit outside everything that makes a table a table:

| | `[Table]` | Prefs |
|---|---|---|
| Part of transactions | yes | no, each write stands alone |
| In the WAL, replay, migrations | yes | no |
| Sent to clients through views | yes | never |
| Indexes, queries | yes | lookup by key only |

So anything players see, or anything that must change together with table rows, belongs in
a table. Prefs are for the server's own bookkeeping.

## Where to reach it

| From | Use |
|---|---|
| Host setup code | `host.Prefs` |
| A general procedure | `ctx.Prefs` |
| A lifecycle hook (`OnInit`, `OnStart`, ...) | `ctx.Prefs`; hooks run before the host exists, and it still works |
| A transaction-only procedure (`{Db}TxCtx`) | not available: it's I/O, and those procedures must stay deterministic |

Don't call prefs from inside a transaction body (`BeginTx(...)`). Read or write before or
after it, and pass values in through args.

## The API

Reads are synchronous: a value is either in memory or read from libmdbx's memory map.
Writes are async, because each one ends with an fsync.

```csharp
// Read: a missing key gives your default, not an error.
Result<string>     GetString(string key, string defaultValue = "")
Result<byte>       GetByte(string key, byte defaultValue = 0)        // also GetSByte, GetInt16, GetUInt16,
Result<int>        GetInt32(string key, int defaultValue = 0)        //      GetUInt32, GetInt64, GetUInt64,
Result<double>     GetDouble(string key, double defaultValue = 0)    //      GetSingle, GetDecimal, GetChar
Result<bool>       GetBool(string key, bool defaultValue = false)
Result<byte[]>     GetBytes(string key)                              // a copy; empty if missing
Result<List<byte>> GetByteList(string key)                           // the same value as GetBytes, as a new list
Result<T>          Get<T>(string key, T defaultValue = default)       // T is a [CustomType]
Result<bool>   HasKey(string key)
Result<IReadOnlyList<string>> Keys()                         // ordinal order

// Write: durable when the task completes.
Task<Result> SetStringAsync(string key, string value, TimeSpan? cacheFor = null)
Task<Result> SetByteAsync / SetSByteAsync / SetInt16Async / SetUInt16Async / SetInt32Async / SetUInt32Async /
             SetInt64Async / SetUInt64Async / SetSingleAsync / SetDoubleAsync / SetDecimalAsync / SetCharAsync /
             SetBoolAsync / SetBytesAsync / SetByteListAsync / SetAsync<T>(...)
Task<Result> DeleteAsync(string key)                          // fine if the key doesn't exist
Task<Result> DeleteAllAsync()

// Several writes in one transaction and one fsync: all land, or none does.
RhinoPrefsBatch Batch()
```

```csharp
var saved = await ctx.Prefs.Batch()
    .SetInt64("season.current", 8)
    .SetString("motd", "Season 8 has started!")
    .Delete("season.7.final-table")
    .CommitAsync();
```

### Value types

Every primitive (`byte`, `sbyte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `float`,
`double`, `decimal`, `char`, `bool`), `string`, raw bytes (`byte[]` or `List<byte>`, stored the
same way and readable either way), and any `[CustomType]` struct:

```csharp
[CustomType]
public readonly partial record struct Schedule(int Season, string League, long StartsAtTicks);

await ctx.Prefs.SetAsync("schedule", new Schedule(8, "Premier", start.Ticks));
var schedule = ctx.Prefs.Get<Schedule>("schedule").Unwrap();
```

Every value remembers its type. Reading it as another type is the error `PrefTypeMismatch`,
never a silent conversion: not `GetString` on a number, not `Get<Money>` on a `Schedule`,
and not even `GetInt64` on a value written with `SetInt32Async`.

### Keys

A key is any non-empty string of at most 255 UTF-8 bytes. Use a dotted convention
(`season.current`, `jobs.daily.lastRun`) to keep them readable in `Keys()`.

## Caching

A value is cached in memory after it's written or first read, and dropped once it has gone
**unused** for its cache duration. Every read or write restarts its clock. A dropped value
is still on disk; the next read loads it again.

- **The default** is `Prefs.CacheDuration` in `rdbsettings.json` (30 minutes;
  [configuration](configuration.md)). It also applies to values written earlier on the
  default: change the setting, and they follow it.
- **Per value:** pass `cacheFor` to the `Set`. It's stored with the value, so it still applies
  after the value is dropped and reloaded, or after a restart.

| `cacheFor` | Meaning |
|---|---|
| `null` (omitted) | the default |
| a positive duration | that duration |
| `TimeSpan.Zero` | never cached: every read goes to disk |
| `Timeout.InfiniteTimeSpan` | never dropped while the process runs |

```csharp
await ctx.Prefs.SetBoolAsync("maintenance", true, cacheFor: Timeout.InfiniteTimeSpan);   // read on every request
await ctx.Prefs.SetBytesAsync("report.cache", bytes, cacheFor: TimeSpan.FromMinutes(2)); // big, rarely read
```

Unused values are swept out every 1-60 seconds (a quarter of the default duration, clamped),
so a short per-value duration can run a few seconds over.

## Errors

Every member returns a `Result`; none throws.

| Kind | When |
|---|---|
| `PrefKeyInvalid` | empty, null or over-long key |
| `PrefTypeMismatch` | the key holds a value of another type |
| `PrefTypeNotRegistered` | `Get<T>`/`SetAsync<T>` with a struct that isn't a `[CustomType]` |
| `PrefCacheDurationInvalid` | a negative `cacheFor` (other than `Timeout.InfiniteTimeSpan`) |
| `PrefsClosed` | the Root database's storage has been closed (shutdown) |
| a storage kind (`ColdStorageFull`, `SystemFailure`, ...) | libmdbx couldn't read or write |

In a batch, one invalid write fails the whole `CommitAsync`, and nothing from the batch is
written.
