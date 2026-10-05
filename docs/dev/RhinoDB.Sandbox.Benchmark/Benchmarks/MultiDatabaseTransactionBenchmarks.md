# MultiDatabaseTransactionBenchmarks

Cost of a multi-database transaction against a plain `BeginTx`, through a real `RhinoHost`
(`BenchHost`: `HostRootDb` Root with Persistent `HostWallet`, keyed `MatchDb` Child with Persistent
`MatchRecord`, singleton `LobbyDb` with Instant `LobbySeat`). Each invocation fans out 1,000 concurrent
transactions (100 for the two-phase cases) and `OperationsPerInvoke` reports the cost **per transaction**,
so tx/sec = 1 / mean. All writes are inserts, `Optimistic`.

| Benchmark | What one transaction does |
|---|---|
| `BeginTxRootNoOp` (baseline) | empty Root `BeginTx` through `RhinoCtx` (typed lambda, the no-table body is otherwise CS0121) |
| `BeginTxRootInsert` | one Root insert |
| `PlannedRootOnly` | `PlanMultiTx`, two Root inserts — one participant, one WAL entry |
| `PlannedRootAndInstantSingleton` | Root insert + singleton Instant insert — two databases, one durable (no 2PC) |
| `PlannedRootAndChildTwoPhase` | Root insert + keyed Child insert — two durable participants, real 2PC |
| `LockedRootOnly` | `LockMultiTx` on the Root, two `Run`s, `Commit` |
| `LockedRootAndChildTwoPhase` | `LockMultiTx` on Root + Child, one `Run` each, `Commit` — 2PC |

## 2026-10-05 (Windows 11, .NET SDK 11.0.100-rc.1.26425.128, BenchmarkDotNet 0.14.0)

| Benchmark | Per tx | tx/sec | Allocated/tx | vs. baseline |
|---|---|---|---|---|
| `BeginTxRootNoOp` | 250 ns | ~4.0M | 176 B | 1.0x |
| `BeginTxRootInsert` | 809 ns | ~1.24M | 736 B | 3.2x |
| `PlannedRootOnly` | 4.80 µs | ~208k | 5.4 KB | 19x |
| `PlannedRootAndInstantSingleton` | 7.39 µs | ~135k | 7.5 KB | 30x |
| `LockedRootOnly` | 5.99 µs | ~167k | 6.0 KB | 24x |
| `PlannedRootAndChildTwoPhase` | 754 µs | ~1,330 | 12.4 KB | ~3,000x |
| `LockedRootAndChildTwoPhase` | 768 µs | ~1,300 | 10.6 KB | ~3,070x |

**Single-participant multi-tx costs ~6x a plain insert per transaction (~3x per row).** A plain `BeginTx`
is one pooled work item the loop runs synchronously. A multi-tx participant is an
`IAsyncExecutionWorkItem` that holds the loop and is driven by instruction round trips over a channel
(acquire → each step → apply → commit), each a `TaskCompletionSource` + cross-thread hop, plus the
`TxValue`/step/participant objects - the ~5 KB/tx. Expected and acceptable for the "few, important"
transactions it's meant for; the obvious levers if it ever matters are fusing steps that target the same
participant into one instruction, and pooling the participant/work-item objects.

**Two-phase commit is fsync-bound and fully serialized: ~1.3k tx/sec regardless of concurrency.** Each
transaction holds the Root (and the Child) *through* its fsynced prepares, so no other transaction can
reach the Root until the disk answers - the WAL's group commit (which gives single-database `Confirmed`
writes ~15k tx/sec in `ConfirmedCoalescingBenchmarks`) never gets two prepares to coalesce. ~750 µs per
transaction is roughly one parallel pair of fsyncs. This is the real price of atomicity across two durable
databases today. The known way past it - not built, a design decision - is early lock release: unlock once
the prepare is *written* but before it's *durable*, and hold back the reply (and any dependent commit) until
the fsync lands, so concurrent 2PC transactions share flushes. The Planned and Locked kinds cost the same
here because the fsync dwarfs everything else.

Adding a second, non-durable database (`PlannedRootAndInstantSingleton`) costs ~2.6 µs over the
single-participant case: one more loop acquired in fixed order, one more step/apply/commit round trip.
