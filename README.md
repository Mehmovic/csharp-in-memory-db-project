# RhinoDB

**An in-memory transactional database for game servers, written in C# (.NET 11).** Single
writer per database, in-memory tables with libmdbx cold storage, a write-ahead log,
multi-database transactions, and client-callable procedures over WebSocket.

> **Status: finished as a learning project, not production-ready, no longer developed.**
> I built RhinoDB to understand how databases work from the inside. I meant to take it all
> the way to production, but along the way I found that [SpacetimeDB](https://spacetimedb.com)
> already is the system I was building toward, so I'm using SpacetimeDB from now on. The
> code stays here, working and tested, for anyone who wants to read how the pieces fit
> together.

## Why I built it

I wanted a server for an online soccer manager game, and I wanted to understand the
database under it instead of treating it as a black box. So I wrote one, bottom up, one
layer at a time, each layer tested before the next one started:

1. **Storage:** dense in-memory tables with stable row offsets.
2. **Indexes:** hash and ordered indexes, unique and non-unique, composite keys, a chunked
   sorted key store with SIMD search, and why each choice costs what it costs.
3. **Transactions:** a single-writer execution loop per database (an actor), staged changes,
   apply/revert with an undo journal, and what "atomic" really requires.
4. **Cold storage:** libmdbx underneath, eviction of rows from memory, checkpoints.
5. **Durability:** a write-ahead log with CRC'd frames, group commit, fsync outside the
   append lock, crash recovery, archives, and pruning.
6. **More than one database:** Root and Child databases, two-phase commit across them,
   crash recovery of half-finished commits, and early lock release with commit
   dependencies.
7. **Networking:** a WebSocket transport, a handshake, and `[Procedure]` methods that
   clients call, with an error contract that never leaks server internals.

Each of these taught me something I couldn't have learned by reading:
- why fsync placement decides throughput;
- why "it passed on Windows" isn't evidence;
- why a crash test is worth ten unit tests;
- how a .NET runtime bug can look exactly like your own memory corruption.

## Why I stopped

The goal was a production-ready engine for my game. The closer I got, the more clearly I
could see that SpacetimeDB already solves the same problem with the same core ideas: data in
memory, one writer per database, transactional functions called by clients, and
subscriptions pushing changes to them. It is further along, maintained by a team, and
already running real games. Finishing RhinoDB to that level, the subscription engine above
all, would have meant spending months rebuilding what already exists instead of building
the game.

So RhinoDB ends here, as the project that taught me what to look for in a database. I'm
continuing with SpacetimeDB.

## What works

Everything below is built and covered by tests. The full suite (~1,500 tests) passes on
Windows and has passed on Linux (WSL).

- **Tables** declared as C# record structs with attributes. Source generators emit the
  typed table operations, indexes, serializers and transaction types. There are two kinds:
  - **Instant:** memory only;
  - **Persistent:** memory plus libmdbx, with eviction.
- **Transactions:** `ctx.BeginTx((db, tx) => ...)` runs on the database's single writer; a
  failed body or apply reverts cleanly.
- **Durability per call:** `Optimistic` (group-committed) or `Confirmed` (waits for fsync).
- **Write-ahead log:** recovery, archives, retention, pruning and replay tools.
- **Schema migration:** contract generations, generated migration chains, and versioned
  client formats (`Raw`, `VersionedMemoryPack`, `MessagePack`).
- **Root and Child databases**, keyed or singleton, activated on demand, each with its own
  loop and WAL.
- **Multi-database transactions:** two-phase commit with crash recovery and early lock
  release ([guide](docs/manual/multi-database-transactions.md)).
- **Procedures** over WebSocket, in two shapes: transaction-only (deterministic, like a
  SpacetimeDB reducer) or general (async) ([guide](docs/manual/procedures.md)).
- **Fail fast:** an unrecoverable engine error exits the process for a supervisor to
  restart ([guide](docs/manual/operations.md)).
- **One settings file** (`rdbsettings.json`), read at startup and by the analyzers at
  compile time ([guide](docs/manual/configuration.md)).
- **Prefs:** a small durable key/value store for server bookkeeping, like Unity's
  PlayerPrefs ([guide](docs/manual/prefs.md)).
- **Benchmarks:** around 2.8-3.0M single-row transactions per second on Instant tables,
  ~1.1M on Persistent with Optimistic durability, and ~12.5k two-phase commits per second,
  all on one development machine.

## What's missing

To be clear about why this is not production-ready:

- **No subscriptions.** Clients can call procedures, but nothing pushes data to them yet.
  That was the next stage, and it is the largest piece SpacetimeDB already has.
- **No authentication.** Every session is anonymous.
- **No communication between separate RhinoDB processes**, no replication, no sharding.
- **Built on a .NET 11 preview** (release candidate 1).
- **Tested on Windows x64 and Linux x64 only.** Prebuilt libmdbx binaries for macOS are
  included but untested.
- **Little-endian machines only**, by design.

## Building and testing

You need the .NET SDK pinned in `global.json` (11.0 RC1 or a later 11.0 feature band).

```
dotnet build RhinoDB.sln
dotnet test RhinoDB.sln
```

The native libmdbx library is prebuilt for Windows x64, Linux x64 and macOS under
`src/RhinoDB.Native/runtimes/`; its sources are vendored in
`src/RhinoDB.Native/vendor/libmdbx/`.

## Repository layout

| Path | What's there |
|---|---|
| `src/RhinoDB.Core` | public types: results, errors, attributes, `RhinoRandom` |
| `src/RhinoDB.Lib` | the engine: storage, indexes, execution, WAL, cold storage, hosting, prefs |
| `src/RhinoDB.Lib.Server` | the network host (Kestrel, WebSocket transport, REST commands) |
| `src/RhinoDB.Generators` | Roslyn source generators and analyzers |
| `src/RhinoDB.Native` | the libmdbx interop layer and vendored libmdbx |
| `src/RhinoDB.Tools.*` | command-line tools: WAL, migrations, contracts |
| `src/*.Test` | the test projects |
| `docs/` | design documents (`00`-`08`), user guides (`manual/`), benchmark notes (`dev/`) |

Start with [docs/00-overview.md](docs/00-overview.md) and
[docs/02-architecture.md](docs/02-architecture.md);
[docs/03-roadmap.md](docs/03-roadmap.md) records what was built when, and why.

## License

RhinoDB is licensed under the **GNU Affero General Public License v3.0** (`LICENSE`) with an
**application exception** (`LICENSE-EXCEPTION.md`):

- **RhinoDB itself is copyleft.** That covers the engine, server, storage, generators,
  tools, and any change to them. If you modify RhinoDB and run it where users reach it
  over a network (a game server, for example), you must make your modified RhinoDB source
  available under the same license.
- **Your application is not covered.** Game logic, table declarations, procedures, and
  client code you build on RhinoDB stay yours, under any license you like.

Third-party code keeps its own license:
- libmdbx, under Apache-2.0 (`src/RhinoDB.Native/vendor/libmdbx/LICENSE` and `NOTICE`);
- MemoryPack and MessagePack-CSharp, under MIT, used as NuGet packages.

## Acknowledgements

- **SpacetimeDB:** its design inspired much of this one (the reducer/procedure split, the
  per-database single writer, the error and ordering rules), and it's where I'm going next.
- **libmdbx:** the storage engine underneath persistent tables.
- **Claude Code (Anthropic):** I built RhinoDB pair-programming with it. I drove the
  design and decisions; it helped plan, write tests and code, and track down the hard bugs.
