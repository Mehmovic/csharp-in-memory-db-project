---
name: rhinodb-dev
description: Operating guide for the RhinoDB repo — an embedded transactional DB in C#/net11.0. Use whenever working on RhinoDB source, tests, docs, benchmarks, or roadmap in this repository: build/test commands, project layout, test-writing patterns, engine rules that are easy to break, the stage-based dependency order, and the collaboration convention (user writes implementation, Claude writes tests/design unless asked to build). Trigger on any RhinoDB source/test/doc change, "run the tests", "run the benchmarks", "next stage", "what's left", or picking up work after a break.
---

# RhinoDB dev guide

## Collaboration convention — check before writing any code

The user writes production/implementation code themselves to learn the domain.
My default role is planning, design-consistency checks, and — the main ask —
writing unit/integration tests (RED first when a stage is being handed off).

Exceptions:
- Small cross-cutting *utility* types with no domain logic to learn
  (`Result`/`Option` in `RhinoDB.Core`-style code) — write/refactor these directly
  when asked to "sketch it" or "write it yourself."
- **An explicit instruction to build, implement or fix** ("build X now", "fix both
  gaps", "implement the per-step apply") — then I write the production code too,
  with tests, and run the full suite. Without such an instruction: offer a plan/test
  spec, let the user implement, review afterward.

The user edits files directly and often mid-task. Treat on-disk changes as
intentional; re-read before editing a file they've touched. Never commit unless asked.

Full detail: memory `feedback-rhinodb-collaboration-style`.

## Build & test

**Pinned to .NET 11 RC1** (`global.json`: `11.0.100-rc.1.26425.128`,
`rollForward: latestFeature`), installed into `C:\Program Files\dotnet` next to the
8/9/10 SDKs. A bare `dotnet` in this repo resolves to it automatically.

```
dotnet build                                   # whole solution (Debug)
dotnet build src/RhinoDB.Lib/RhinoDB.Lib.csproj -c Release   # catches #if DEBUG-only API (e.g. MultiTxCrashPoint, TestOnly* hooks)
dotnet test                                    # whole solution — always, after every slice
dotnet test src/RhinoDB.Generators.Test --filter "FullyQualifiedName~MultiTransactionTests"   # one fixture while iterating
```

Output is condensed by RTK. Totals: `dotnet test -v q | grep -E "Passed!|Failed!"`;
failure details: `rtk proxy dotnet test <proj> --logger "console;verbosity=detailed"`.
When using `--no-build`, confirm the build actually succeeded first
(`rtk proxy dotnet build -v q | grep -E "Error\(s\)|Build succeeded"`) or the tests run stale binaries.

**11 test projects**, one per production project, suffix `Foo.Test` (never `Test.Foo`;
test namespace = project name): Core, Lib, Lib.Server, Native (2 skipped by design),
Generators, PreBuild, SchemaContracts, Tools.Contract, Tools.Dev, Tools.Migration, Tools.Wal.
Full suite as of 2026-10-05: **1,308 passed, 2 skipped**. Never let two stages go red at
once — full-solution green is the gate before the next slice.

## Project layout

```
src/RhinoDB.Core            — pure contracts (Result/Option/DbError/exceptions, attributes), zero deps
src/RhinoDB.Lib             — the engine: Storage/, Indexing/, Tables/, Execution/ (DbContext, DbExecutionLoop,
                              PooledOperation, RhinoCtx, PlannedMultiTx/LockedMultiTx), Cold/ (ColdStore),
                              Durability/ (WAL, ChainLog, checkpoint), Hosting/ (RhinoHostBuilder, Children)
src/RhinoDB.Lib.Server      — network host (loaded by reflection if referenced)
src/RhinoDB.Native          — raw P/Invoke to vendored libmdbx; the ONLY project with AllowUnsafeBlocks
src/RhinoDB.Generators      — Roslyn source generators (netstandard2.0), referenced as an Analyzer
src/RhinoDB.SchemaContracts — shared by generators + tools (DatabaseDiscovery, TableIdHash, config/descriptors)
src/RhinoDB.PreBuild        — MSBuild pre-build step (.props/.targets imported by consumers)
src/RhinoDB.Tools.*         — Contract / Dev / Migration / Wal CLIs
src/RhinoDB.Run.Cli         — CLI host
src/RhinoDB.Sandbox*        — playgrounds (Sandbox, Sandbox.Benchmark, Sandbox.Lib.Server, Sandbox.MigrationFixture)
```

`src/RhinoDB.Run.Server`, `Run.Server.Benchmark`, `Run.Server.Sandbox` are **empty leftovers**
(bin/obj only, not in the .sln) from an old naming; the root `BenchmarkDotNet.Artifacts/` is
stale output from them. Don't treat either as current.

`RhinoDB.Native/vendor/libmdbx/` is vendored C source + `build.zig`. Read
`vendor/libmdbx/VERSION.txt` before ever touching it. Built binaries under
`runtimes/{rid}/native/` are committed; a normal build never invokes Zig.

## Where decisions live — don't re-derive, don't duplicate

The docs folder is **`docs/`** (lowercase in git; Windows also resolves `Docs/`).

- `docs/03-roadmap.md` — the stage tracker. Check it before starting new work.
- `docs/02-architecture.md`, `docs/01-performance-principles.md`, `docs/05-wal-design.md`,
  `docs/04-networking.md` — the "why" behind engine decisions.
- **`docs/manual/`** — user-facing "how to use it" guides (first one:
  `multi-database-transactions.md`). A request for "a complete doc" about a feature means a
  manual page here, linked from the matching design section — not a design-doc section.
- **The active plan file**: `~/.claude/plans/shimmying-sniffing-blum.md` — the authoritative
  task breakdown (Part J: `[Procedure]` two shapes, singleton Children, multi-db transactions,
  per-step apply, open items). Read its Status header first; update it when a piece lands.
  Older entries keep historical names on purpose — only current-state sections use current names.
- The live Claude artifact is deprecated (2026-09-07) — don't fetch or cite it.
- Memory entries are point-in-time snapshots — verify against code before asserting an API exists.

## Comments — lean, rationale in `docs/Dev/`

The original rule was "production `.cs` files are comment-free; design rationale lives in
`docs/Dev/`, mirroring `src/` 1:1" (`src/RhinoDB.Lib/Hosting/ChildDatabaseRegistry.cs` →
`docs/Dev/RhinoDB.Lib/Hosting/ChildDatabaseRegistry.md`). In practice production code now
carries a small number of short "why" comments (~130 lines across Lib/Core/Generators as of
2026-10-05), and the user trims them. So: keep new production comments rare and one-to-two
lines, never narrate the obvious, put anything longer in `docs/Dev/`. "Clean up comments"
means sweep every production file touched in the session and move rationale to `docs/Dev/`
1:1 (memory `feedback-rhinodb-docs-dev-comment-cleanup`). Test files: normal comments.

## Engine rules that are easy to break

- **Result-returning APIs never throw** — every failure, misuse included, is a `DbError` kind
  (new kinds: a `[GenerateDbError]` exception class in `src/RhinoDB.Core/Exceptions/`
  generates `DbError.X()` + `ErrorKind.X`). Throw only from members that can't return a
  Result (fluent `Add`, `void Rollback`, property getters).
- **One Root per host** (`AddDatabase`). **Hooks are Root-only** (`[OnInit<ChildDb>]` → RHINO039).
- **Children**: keyed `[ChildDatabase<TRoot,TKey>]` (lazy, disposable) and singleton
  `[ChildDatabase<TRoot>]` (key `SingletonChild.Key = "singleton"`, activated at build in Run
  mode, never disposed). Generators discover both through `DatabaseDiscovery.ChildDatabases(...)`
  — use it, never `ForAttributeWithMetadataName` on one arity. Children **auto-load** Persistent
  tables on activation (`DbContext.LoadFromColdAsync`, overridable via `options.Load`); the Root
  does not — callers still run `new {Root}Loader().LoadAsync(...)`. Never load a Child by hand
  (a second bulk load duplicates rows).
- **Multi-database transactions** (`PlanMultiTx`/`LockMultiTx`): fixed lock order (Root, then
  Children by path), steps run in call order and **apply per step** (retained undo journal, one
  LSN per scope), 2PC only with ≥2 durable participants, crash recovery completes anything whose
  prepares are all on disk. User guide: `docs/manual/multi-database-transactions.md`.
- An operation **returns its result before its trailing sweep** (`PooledOperation.Run`) —
  anything reading table state outside the loop races it.
- `[Procedure]` (planned, not built): one attribute, two shapes — `(RhinoCtx ctx, ...)` general,
  `(SomeDbTransaction tx, ...)` transaction-only (Root or singleton Child only). `[Transformer]` is dropped.

## Writing tests — patterns that work

- **Generator/integration tests** compile a source string with `GeneratorTestHost.CompileAndLoad(Source)`
  and call into a `public static class TestHelpers` inside that source via
  `GeneratorTestHost.InvokeHelper(asm, "TestNs.TestHelpers", "Method", args)` — the row/db types only
  exist in the compiled assembly, and ref structs (`QuerySet`/`QuerySingle`) can't cross
  reflection/`dynamic`, so reads stay inside helpers (memory `project-rhinodb-query-result-ref-struct-testing`).
- **Host-level tests** (see `MultiTransactionTests`, `SingletonChildDatabaseTests`): per-test temp dir +
  an `rdbsettings` file written with `RhinoDbConfig`/`GeneratorConfigLoader.ConfigFileName`; a
  `Shutdown(host)` that disposes the host *and* the Root's internal `ColdStore` (reflected `Cold`
  property) so the same dir can be reopened for restart/crash tests.
- **Never assert inside a `Run`/`BeginTx` body.** The operation catches whatever the body throws
  (a failed Assert, a missing probe) and turns it into an error `Result` — the test passes having
  checked nothing. Return values out, or read state off the database between operations (after a
  no-op "settle" operation, because of the trailing-sweep race above).
- Crash simulation: `TestOnlySimulateCrashAt = MultiTxCrashPoint.AfterFirstDurablePrepare /
  AfterAllDurablePrepares` (DEBUG only). Fault injection: `TestOnlyApplyFault`/`TestOnlyRevertFault`
  on table ops, `Wal.TestOnlyBeforeFlush`.
- Expected compile errors: `Assert.Throws<InvalidOperationException>(() => CompileAndLoad(src))`
  and check the message for the `RHINOxxx`/`CSxxxx` id — and make sure that id is the *only* reason
  (e.g. a Child with no Persistent table used to fail for an unrelated ctor error).
- New diagnostic ids: register them in `src/RhinoDB.Generators/AnalyzerReleases.Unshipped.md`.

## Benchmarks

`src/RhinoDB.Sandbox.Benchmark` (BenchmarkDotNet, the only real benchmark project):
```
dotnet run -c Release --project src/RhinoDB.Sandbox.Benchmark -- --filter "*ThroughputBenchmarks*" --artifacts <scratch dir>
```
`ThroughputBenchmarks` = 10,000 concurrent single-row transactions per invocation
(ops/s = 10,000 / mean). Also `SubmissionOverheadBenchmarks` (per-call cost) and
`ConfirmedCoalescingBenchmarks` (Confirmed mode), `MultiDatabaseTransactionBenchmarks` (per-tx cost of
planned/locked/2PC vs `BeginTx`), `ChildColdActivationBenchmarks`/`ChildLifecycleBenchmarks` (activation,
auto-load, create/dispose) — the last three run a real `RhinoHost` via `BenchHost`. Baselines and writeups live in
`docs/Dev/RhinoDB.Sandbox.Benchmark/Benchmarks/*.md` (last throughput baseline:
2026-09-20, Instant ~2.8-3.0M ops/s, Persistent Optimistic ~1.10-1.19M ops/s). Runs take
several minutes — run in the background; point `--artifacts` at a scratch dir, not the repo root.

## Known, intentional gaps — don't "fix" these without asking

- **Secondary-index uniqueness is in-memory-only; nothing enforces it against
  libmdbx.** Cold storage is keyed purely by primary key. Any row not currently
  loaded sits outside every unique secondary index's enforcement scope; a later
  `Insert` reusing that "unique" value silently produces a durable duplicate.
  Deliberate (no cold-storage probing on `Insert`/`Evict`). Full detail:
  `docs/02-architecture.md` § Cold storage.
- **Read-your-own-writes inside one operation is not supported** (overlay removed
  2026-09-18): a staged write is invisible to reads until applied. The one exception is
  multi-database transactions, where each *step* applies before the next.
- On hold by user decision: the conflict/retry API for multi-db transactions; a timeout or
  re-entry guard on `LockMultiTx` (the cost is "known to the developer").

## Git commit style observed in this repo

`[tag] short description`, lowercase tag, imperative-ish: `[add]`, `[update]`, `[refactor]`,
`[feature]`, `[doc]`, `[test]`, `[fix]`, `[cleanup]`. Match it rather than inventing a new style.

## Small habits that save a round trip

- When a design tangent comes up mid-task, ground the tradeoff in the actual code (read the
  file first) — docs and memory lag behind a fast-moving repo.
- Naming matters to the user: names should convey intent (e.g. `PlanMultiTx`/`LockMultiTx`,
  `RhinoCtx` to avoid framework collisions) and stay readable on disk (`Children/{Db}/singleton/`,
  not a hash). Offer 2-4 named options with a recommendation when a rename comes up.
- Before claiming a perf change helps, measure it (a tiny Release console in the scratchpad is
  fine for a micro-question; BenchmarkDotNet for engine throughput).
- Future/speculative ideas the user wants kept informal belong in memory as a `project` entry,
  not an unrequested roadmap edit.
