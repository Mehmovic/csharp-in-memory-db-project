# Configuration: `rdbsettings.json`

Every RhinoDB option lives in one file, `rdbsettings.json`, next to your project file.
The same file is read twice:

- **at compile time:** the generators and analyzers read it as an `AdditionalFile`
  (added by `RhinoDB.PreBuild.props`). That's how a project's choices become build
  errors instead of runtime surprises. Example: `Network.Transport` is `WebSocket`, so
  using `Delivery.Unreliable` fails the build with **RHINO040**.
- **at startup:** `RhinoHostBuilder.BuildAsync` reads it from the config directory
  (`AppContext.BaseDirectory` by default) to configure the host.

If the file is missing, RhinoDB writes one containing every option at its default value,
so you can see everything that can be set. Changing a value means editing the file and
rebuilding/restarting. Nothing is reloaded live.

The only settings that stay in code are the ones that are code: callbacks such as
`OnUnrecoverableError(UnrecoverableErrorPolicy.Callback(...))`,
`SetAppVersionValidator(...)` and the `CreateDb` / `Load` factories.

## The full file, with defaults

```json
{
  "Generator": {
    "SourceParentDirectory": "RhinoContracts",
    "TargetParentDirectory": "RhinoDB",
    "SchemaDescriptorPath": "RhinoContracts/Descriptor.json",
    "MigrationsOutputDirectory": "Migrations",
    "ClientProtocol": "Raw",
    "DisableContractAutoPreBuild": true
  },
  "Server": {
    "Version": "0.0.0",
    "ArchiveRetention": { "Enabled": false, "Interval": null, "KeepFor": null }
  },
  "Host": {
    "ColdPath": null,
    "Mode": "run",
    "ReplayUpToLsn": null,
    "WalKeepGenerations": null,
    "WalPruneOlderThan": null,
    "HttpPort": 7777,
    "HttpEnabled": null,
    "UnrecoverableError": { "ShutdownBudget": "00:00:05", "ExitWatchdog": "00:00:07" }
  },
  "Network": {
    "Transport": "WebSocket"
  },
  "Durability": {
    "WalFlushThresholdBytes": 4194304,
    "WalFlushInterval": "00:00:00.100",
    "EvictionBatchThresholdBytes": 4194304
  }
}
```

Durations use `[d.]hh:mm:ss[.fff]`, for example `"00:00:05"` (five seconds) or
`"30.00:00:00"` (30 days).

## `Generator`: code generation (compile time)

| Option | Default | Meaning |
|---|---|---|
| `SourceParentDirectory` | `RhinoContracts` | Where the shorthand table-contract files live. |
| `TargetParentDirectory` | `RhinoDB` | Where the expanded contracts are written. |
| `SchemaDescriptorPath` | `RhinoContracts/Descriptor.json` | The frozen schema descriptor used for migrations. |
| `MigrationsOutputDirectory` | `Migrations` | Where generated migrations go. Compiled, so not under `RhinoContracts`. |
| `ClientProtocol` | `Raw` | How rows and procedure arguments are encoded for clients: `Raw`, `VersionedMemoryPack` or `MessagePack`. Set once per project. |
| `DisableContractAutoPreBuild` | `true` | When `true`, you run `rhinodb contract generate` yourself instead of on every build. |

## `Server`: what this server is

| Option | Default | Meaning |
|---|---|---|
| `Version` | `0.0.0` | `major.minor.patch`; sent to clients and readable as `ctx.ServerVersion`. |
| `ArchiveRetention.Enabled` | `false` | Periodically delete archived WAL generations older than `KeepFor`. |
| `ArchiveRetention.Interval` | — | How often the collector runs. Required when enabled. |
| `ArchiveRetention.KeepFor` | — | How much archive history survives. Required when enabled. |

## `Host`: how this process runs

| Option | Default | Meaning |
|---|---|---|
| `ColdPath` | per-user app data `RhinoDB/{project}` | Where the database files live. Children live under `{ColdPath}/Children/`. |
| `Mode` | `run` | `run`, `replay`, `migrate`, `wal-prune` or `wal-migrate`. |
| `ReplayUpToLsn` | — | `replay` only: stop at this LSN. |
| `WalKeepGenerations` / `WalPruneOlderThan` | — | `wal-prune` only: keep the last N generations, or everything newer than a timestamp. Give one, not both. |
| `HttpPort` | `7777` | The port for WebSocket and REST. `0` picks a free port. |
| `HttpEnabled` | `true` in `run`, `false` otherwise | Start the network host. |
| `UnrecoverableError.ShutdownBudget` | `00:00:05` | After an unrecoverable error: how long to spend closing connections and flushing healthy WALs before exiting. See [operations](operations.md). |
| `UnrecoverableError.ExitWatchdog` | `00:00:07` | If the exit itself hangs this long, the process is killed with `FailFast`. |

## `Network`: the client transport (compile time and startup)

| Option | Default | Meaning |
|---|---|---|
| `Transport` | `WebSocket` | The only transport today. WebSocket runs over TCP, so delivery is always reliable and ordered. Any `Delivery` other than `ReliableOrdered` is a **compile error (RHINO040)** while it's selected. |

## `Durability`: WAL flushing (startup)

These apply to the Root and to every Child database.

| Option | Default | Meaning |
|---|---|---|
| `WalFlushThresholdBytes` | `4194304` (4 MiB) | `Optimistic` writes are flushed to disk once this many bytes are buffered... |
| `WalFlushInterval` | `00:00:00.100` | ...or after this long, whichever comes first. This is the window of `Optimistic` writes a crash can lose. `Confirmed` writes always wait for their own fsync. |
| `EvictionBatchThresholdBytes` | `4194304` (4 MiB) | How many bytes of evicted rows are batched before being written to cold storage. |

## Errors

- **At startup,** an invalid value (an unknown `Mode` or `Transport`, a malformed
  duration, a non-positive size) makes `BuildAsync` return a `SystemFailure` error that
  names the setting. Exit non-zero on it; see [operations](operations.md).
- **At compile time,** a malformed file is a **RHINO024** warning. Every setting then
  falls back to its default, so the build doesn't stop, but fix the file: the defaults
  may not be what you meant.
