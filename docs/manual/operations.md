# Running RhinoDB in production

RhinoDB is built to run **under a supervisor**: systemd, Docker or Kubernetes, or a
Windows service. When the engine hits an error it can't recover from in place, it
**exits with a non-zero code**, and restarting it is the supervisor's job. A fresh
process recovers everything durable from disk.

## What counts as unrecoverable

A database becomes **poisoned** when the engine can no longer trust that what it holds
in memory matches what's on disk. It then refuses all further work, and the process
exits. Today that happens in exactly these cases:

| Cause | Typical reason | Exit code |
|---|---|---|
| A WAL write or fsync failed (`WalDurabilityFailed`) | disk full, I/O error, storage gone | **74** |
| A WAL or cold-storage directory sync failed | same | **74** |
| A multi-database transaction's outcome is unknown (`MultiTxOutcomeUnknown`) | a prepare failed *and* the abort couldn't be recorded: two disk failures | **74** |
| An apply failed and undoing it also failed (`ApplyFailed`) | an engine bug, or running out of memory mid-apply | **70** |

74 is `EX_IOERR` (look at the disk) and 70 is `EX_SOFTWARE` (report a bug). RhinoDB
never exits 0 for a failure.

**Everything else is an ordinary error, not a reason to exit.** A transaction body
returning an error or throwing, a constraint violation such as a duplicate key, a
rolled-back or aborted transaction, misuse of an API: all of these return an error
`Result`, and the database keeps serving. Game logic and user mistakes can't take the
engine down. Only the disk or an engine bug can.

**Any poisoned database stops the whole engine,** the Root or any Child. A Child can't
be reopened from disk on its own, and a Child's WAL failing usually means the shared
disk is failing too.

## What happens on exit

The first poison triggers this exactly once:

1. One line to stderr, for example:
   `RhinoDB: unrecoverable error in RootDb (Root): WalDurabilityFailed - ... Exiting with 74 so the supervisor restarts the engine.`
2. A bounded best-effort shutdown (about 3 seconds): the network host closes, so
   clients see a clean disconnect, and every other database's buffered writes are
   flushed to disk.
3. The process exits with the code above. If something hangs during exit, a watchdog
   forces it down so you never end up with a process that's alive but refusing work.

The request that hit the failure still gets its error before the exit.

## What a restart keeps and loses

| | After a restart |
|---|---|
| `Persistent` tables | **kept**: everything is recovered from the WAL and libmdbx at startup |
| Multi-database transactions | finished or dropped consistently on every database (see [multi-database-transactions.md](multi-database-transactions.md) §9) |
| `Optimistic` writes from the last moment | may be lost, as with any crash (that's what `Optimistic` means) |
| `Instant` tables | **empty**, since they live in memory only |
| Connected clients | disconnected; they must reconnect |

## Startup failures

If the engine can't start, for example because recovery can't write to a full disk,
`RhinoHostBuilder.BuildAsync()` returns an error. **Your host program must then exit
non-zero too**, so the supervisor sees the failure:

```csharp
var built = await RhinoHostBuilder.Create().AddDatabase<GameDb, GameDbTransaction>(...).BuildAsync();
if (built.IsError()) {
    Console.Error.WriteLine($"Failed to start: {built.GetError().ToException().Message}");
    return 1;
}
```

## Supervisor setup

Restart on failure, **with backoff**. A full disk makes every restart fail again, and
an instant tight loop only fills the logs.

**systemd**

```ini
[Unit]
StartLimitIntervalSec=300
StartLimitBurst=5

[Service]
ExecStart=/usr/bin/dotnet /opt/game/GameServer.dll
Restart=on-failure
RestartSec=5
```

**Docker / Compose** (Kubernetes restarts failed containers with `CrashLoopBackOff` by default)

```yaml
services:
  game:
    image: game-server
    restart: on-failure
```

**Windows service:** in the service's *Recovery* tab, set the first, second and
subsequent failures to *Restart the Service*, with a delay of a few seconds. Make
sure "Enable actions for stops with errors" is checked.

Alert on exit code 74 (check the disk) and 70 (file a bug with the stderr line).

## Embedding and tests

To keep the process alive, for example in tests or a host that manages failure
itself, replace the policy:

```csharp
RhinoHostBuilder.Create()
    .OnUnrecoverableError(UnrecoverableErrorPolicy.Callback(error =>
        logger.Critical($"{error.Database}: {error.Error.Kind} (would exit {error.ExitCode})")))
    ...
```

The callback runs once, off the database's thread. The poisoned database still refuses
all work, so it's your job to stop the process when you're ready.
