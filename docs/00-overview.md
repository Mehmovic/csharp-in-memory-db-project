# RhinoDB — Overview

## What we're building

RhinoDB is an **embedded database engine tailored for online games**, for C# (net11.0).
It is a library, not a standalone server: the application hosts it in-process, so a
call into the engine is a function call, not a network request.

Two table kinds cover every storage need:

- **`persistent`** — durable, backed by libmdbx. Survives a restart.
- **`instant`** — in-memory only. Fast, volatile, gone on restart.

Whether a table is persistent or instant is a declaration choice. Whether a given
*call* is transactional is a separate, per-call decision — any call can open a
transaction explicitly, and a single-operation write to a persistent table gets a
no-ceremony `.Atomic` path that opens+commits a transaction internally.

## Hosting: the application owns the process

The host application always runs an HTTP server, the way ASP.NET Core applications do:

- **REST** — administration and the command centre.
- **Realtime** — the upgrade endpoint the subscribe-and-diff layer rides on. WebSocket is
  the first transport; WebTransport and a reliable-UDP layer are possible later behind the
  same abstraction, and only those transport implementations may know the HTTP server
  exists. The subscription and fan-out code is written against the transport abstraction
  and never against HTTP.

`RhinoDB.Lib` does not reference ASP.NET Core, which is what keeps that separation real.
The offline CLI tools (`contract`, `migration`, `wal`, `dev`) never start a server at all.

**One data directory belongs to one process.** The engine holds its write-ahead log
exclusively and libmdbx takes its own locks, so two processes must never be pointed at the
same directory. "Embedded" means exactly that: the data lives inside the application that
owns it. Scaling is therefore more `DbContext`s in one process, not more processes against
one file.

## Why this exists

The target use case is latency-sensitive, stateful applications — game servers and
simulations — where a client-server round trip to an external database is the wrong
shape for the hot path (e.g. "update 200 entity positions this tick"), but where the
durable side of the same application (accounts, standings, transaction history) still
needs real ACID guarantees. Most embedded/in-memory databases force a choice between
those two needs; RhinoDB's per-table durability tier is the mechanism that avoids that
tradeoff — durable and in-memory data live side by side in the same process, the same
transaction model, the same API shape.

## The real target: an online soccer manager game

RhinoDB isn't a generic database built speculatively — it's being built to run an
online soccer manager game, and that target shapes almost every design decision:

- **Persistent tables**: clubs, finances, contracts, standings, transfer history.
- **Instant tables**: live match tick state — ball/player positions, stamina.
- **Transactions**: a player transfer must debit the buyer, credit the seller, and
  reassign the contract atomically — a textbook multi-table transaction.
- **Change propagation**: online multiplayer needs match state and league updates
  pushed to clients, with different delivery guarantees per table — reliable for
  standings, lossy-ok-if-dropped for ball position on a given tick.

This is a committed multi-month project, not a proof of concept. The scope is real
because the game needs all of it, not because the design is over-built for a smaller
goal.

## How to read these docs

- **[Performance Principles](01-performance-principles.md)** — the non-negotiable
  rules (data-oriented layout, no closures, no boxing) and why source generators are
  the tool that lets us have both a nice API and zero-allocation hot paths.
- **[Architecture](02-architecture.md)** — the core model: table kinds, transactions,
  execution model, storage engine, indexing, cold storage.
- **[Roadmap](03-roadmap.md)** — the build order, current status, and what's next.
- **[Networking](04-networking.md)** — the realtime layer: sessions and frames, transport
  abstraction, the REST command centre, and the boundary that keeps the HTTP server out of
  the fan-out code.
- **[WAL design](05-wal-design.md)** — durability: group commit, checkpoints, the archive,
  time-based retention, and why reads never touch the log.
- **[Schema migration](06-schema-migration.md)** — contract generations and how an on-disk
  format is migrated at startup.
- **[Client codegen](07-client-codegen.md)** — generating client bindings and their codec.
- **[Debug session capture](08-debug-session-capture.md)** — capturing and replaying a live
  session for diagnosis.

These `Docs/` files are the authoritative, continuously-updated design — not a
summary of something more detailed living elsewhere. Design work used to be
drafted in a Claude artifact ("RhinoDB — Design Notes v0.2") before this repo's
own docs existed; that artifact is now deprecated (2026-09-07) — its content has
been folded into these files where still valid, and its transaction/execution
pseudocode was superseded outright by the real `DbContext`/`DbExecutionLoop`
implementation (Stage 4) and by [Architecture](02-architecture.md)'s own
description of it. Don't re-fetch or cite the artifact going forward; treat
these docs, plus the actual source under `src/`, as the complete picture.
