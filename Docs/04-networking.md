# Networking — REST command center + transport-agnostic real-time layer

> **TEMP/DRAFT — 2026-09-14, not yet wired into the other docs.** Read-first
> copy only: [Overview](00-overview.md), [Architecture](02-architecture.md),
> and [Roadmap](03-roadmap.md) do not reference this file yet. Once reviewed
> and agreed, this banner comes off and Stage 7/8's roadmap entries, the
> Hosting section, and the overview's doc list get pointed here.

**Designed 2026-09-14, not yet built.** This is the Stage 7/8 networking design,
superseding the bare "minimal direct HTTP/WebSocket host" sketch in
[Architecture — Hosting](02-architecture.md#hosting) (that section's core stance
— mature libraries used as-is, no framework overhead — is unchanged; this doc
adds the *shape* that stance lacked). Read with
[Roadmap — build order](03-roadmap.md) and
[Architecture — Client access / three serialization pipelines](02-architecture.md#three-serialization-pipelines-not-one).

## The decision (2026-09-14)

Networking splits into **three concerns with two different abstractions**, not
one HTTP-shaped umbrella:

1. **Command center (REST)** — request/response, client-initiated, stateless
   semantics. ASP.NET Core **Kestrel + minimal APIs**, used as-is. Auth, admin,
   matchmaking, account endpoints — the non-realtime surface.
2. **Real-time layer** — long-lived sessions, server-initiated pushes,
   subscribe-and-diff (Stage 8's model, unchanged). **This is where the
   transport abstraction lives.**
3. **The HTTP server itself** — an *implementation detail* of 1 and 2 today,
   not a concept either of them knows about.

The real-time layer is built **over** WebSocket (via Kestrel) first, but is
**agnostic to HTTP**: later transports — WebTransport (HTTP/3), then a
raw/reliable-UDP layer — are their own hosts behind the same abstraction, and
nothing above the seam changes. Kestrel should appear in exactly one file.

### The agnosticism test

Grep the fan-out/subscription code for `Http` — zero hits is the bar. The
abstraction is defined in terms of **messages over a session** (frames,
identity, delivery classification, backpressure), never in terms of HTTP
concepts (routes, verbs, headers, status codes). If a design question is being
answered with HTTP vocabulary, that question belongs to a specific transport
implementation, not the seam.

## The seam: what the abstraction actually is

The lowest common denominator of WebSocket / WebTransport / RUDP:

- **A session** — a durable, addressed, bidirectional relationship with one
  client (auth, identity, interest set). Not a connection: a session survives
  reconnect (see Resume below); connections come and go under it.
- **Frames** — small, typed, bidirectional messages. `ushort Type`
  discriminator (a protocol constant, not a URL) + `ReadOnlyMemory<byte>`
  payload. No HTTP semantics inside.
- **Delivery classification** — `ReliableOrdered` vs. `Unreliable` as part of
  the frame model from day one. This is the one thing cheapest to retrofit
  *never*: WebSocket has only one channel (reliable-ordered — its "unreliable"
  maps to reliable-but-droppable-priority today, explicitly), WebTransport has
  three, RUDP is configurable. The fan-out engine classifies traffic (reliable
  standings diffs vs. lossy tick state — the split
  [Overview](00-overview.md#the-real-target-an-online-soccer-manager-game)
  already names) against the model, not against WebSocket's accident of a
  single channel.
- **Backpressure** — the consumer (fan-out) must be able to slow down or drop
  per-priority, not buffer forever.
- **Identity crosses the seam; HTTP mechanics don't.** Every transport must
  establish "this session belongs to principal X" — HTTP does it via
  cookie/bearer, UDP later via a handshake token. So `Session.PrincipalId` is
  part of the abstraction; *how* it was established is transport-local.

Sketch (deliberately rough — shape only, not a frozen API):

```csharp
public interface IRealtimeTransport {
    event SessionAccepted? OnSessionAccepted;          // transport raises after auth completes
    ValueTask SendAsync(SessionId session, Frame frame,
        Delivery delivery, CancellationToken ct);
}

public readonly record struct Frame(ushort Type, ReadOnlyMemory<byte> Payload);

public sealed class Session {
    public SessionId Id { get; }
    public PrincipalId Principal { get; }   // crosses the seam; establishment is transport-local
}
```

### Identity is shared with REST — one principal, two hosts

The command center and the real-time layer must resolve to the **same
principal type from the same token format**, or two identity systems accrete.
One `PrincipalId`, resolved once per request/session, shared by both hosts.
Auth *endpoints* live in REST (login/token issue); the real-time layer only
*validates*.

## The protocol over the frames

A small typed protocol, independent of encoding — the frame `Type` enum is
what the client-access codec decodes into (Stage 8's separate wire-format
declaration: `VersionedMemoryPack`/`MessagePack` — unchanged, see
[Architecture's three-serialization-pipelines
section](02-architecture.md#three-serialization-pipelines-not-one)):

- `Subscribe(view, keyset?)` / `Unsubscribe`
- `DiffBatch(cursor, changes[])` — the only server-initiated data path
- `Heartbeat` / `Ack`
- **`Resume(session, cursor)`** — reconnect/catch-up as a *protocol* concept:
  "resume session X at cursor Y." Cursors are transport-agnostic; TCP-level
  reconnect is transport-local. This framing is what keeps reconnect/catch-up
  (Stage 8's "real scope" item) portable across future transports.

## Build discipline (learned from Stage 6's scope correction)

- **Extract the interface from two real implementations, not from the
  abstract.** `WsTransport` is written as a concrete type with a narrow surface
  shaped like `IRealtimeTransport`; the second transport (WebTransport or RUDP)
  is what settles the interface's actual members. A single-implementation
  interface designed in the abstract is wrong in ways two implementations
  correct — the same lesson as "change propagation" having been one ambiguous
  stage.
- **Don't build unreliable delivery ahead of evidence** — same gate the
  roadmap's UDP backlog entry already applies. The `Delivery` classification
  exists in the model now; the second channel is built when a transport that
  has one actually arrives.
- **Fan-out first, against a fake transport.** The hard component is
  subscription matching (routing `Change<TKey,TRow>` streams to per-session
  interest sets, last-write-per-key merge) — pure in-process logic, testable
  with zero real transports. Same "prove by hand, then wire" pattern the pool
  and `PersistentTable` used.
- **Pooling applies directly** — per-connection send buffers are the exact
  `ArrayPool<T>` fit the backlog already names; frames rent, transport
  implementations return.

## Relationship to existing decisions

- **IDC is a different transport with a different interface**
  (`IIdcTransport`, table-keyed peer delivery) for a different audience
  (trusted, same-operator actors). Do not unify the two abstractions — a
  database's peers and its clients have different trust, delivery, and
  versioning constraints (Architecture, three-serialization-pipelines).
- **Hosting section's "no full framework" stance**: minimal APIs + Kestrel as
  *host*, no controller/DI/middleware ceremony beyond what hosting WebSockets
  needs. The real-time layer adds no HTTP middleware of its own.
- **Deployment neutrality unchanged**: RhinoDB hosts nothing; the game server
  process owns Kestrel and any future transport hosts.


## Stage 8 scope boundary — what the WAL/ring provides vs. what this builds (2026-09-14)

The WAL + change-ring design (see the TEMP draft `05-wal-design.md`) provides
the **supply side** of client propagation: an LSN-indexed, operation-granular
change stream per database, with RAM-fast reconnect catch-up (ring) and
durable fallback (WAL tail), covering Instant and Persistent tables alike.
It does **not** provide Stage 8 itself. What this doc's real-time layer still
builds on top of it:

1. **Subscription matching / fan-out** — routing ring entries to per-session
   interest sets (views, keysets), per-session merge, backpressure per
   priority. The ring is the input, not the substitute; this is the biggest
   unbuilt risk in the project.
2. **Initial snapshot** — first-time subscribers get current view state
   serialized at the same serialization point as their starting cursor, then
   diffs (memory scan + cold fallback for evicted rows — the WAL design's
   merged cold+memory rule).
3. **Lossy delivery** — the `Delivery` classification in the frame model maps
   to reliable-ordered via the ring/fan-out today; lossy tick-state push is
   fan-out policy, not ring behavior.
4. **Client schema/codec** — generated from `[Table]`/command attributes (the
   generator is the schema authority): schema descriptor + per-language codecs
   (C# first, TS for web clients later). Rows are product types, so
   MessagePack-plus-generated-schema is the default; a SpacetimeDB-style
   algebraic type system stays backlogged until a real client language and a
   demonstrated shortfall — the one place to watch is client→server commands,
   which are naturally sum-typed.

Protocol note: `Resume(session, cursor)` above is defined against the WAL
ring's LSN space — the cursor *is* the LSN (operation-granular, so resuming
never tears a multi-table operation). This is why the WAL design resolved LSN
granularity to "one LSN per `Run` call."
