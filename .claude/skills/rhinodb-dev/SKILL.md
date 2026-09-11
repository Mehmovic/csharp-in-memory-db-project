---
name: rhinodb-dev
description: Operating guide for the RhinoDB repo — an embedded transactional DB in C#/net11.0. Use whenever working on RhinoDB source, tests, docs, or roadmap in this repository: build/test commands, project layout, the stage-based dependency order, and the collaboration convention (user writes implementation, Claude writes tests/design). Trigger on any RhinoDB source/test/doc change, "run the tests", "next stage", "what's left", or picking up work after a break.
---

# RhinoDB dev guide

## Collaboration convention — check before writing any code

The user writes production/implementation code themselves to learn the domain.
My default role is planning, design-consistency checks, and — the main ask —
writing unit/integration tests (RED first when a stage is being handed off).

Exception: small cross-cutting *utility* types with no domain logic to learn
(`Result`/`Option` in `RhinoDB.Core`-style code) — write/refactor these directly
when asked to "sketch it" or "write it yourself," no need to wait for a second
confirmation. Everything else: offer a plan/test spec, let the user implement,
review afterward.

Full detail: memory `feedback-rhinodb-collaboration-style`.

## Build & test

**Pinned to .NET 11 RC1 (`global.json`, 2026-09-11)** — installed user-locally
to `%LocalAppData%\Microsoft\dotnet` (`-NoPath`, deliberately not touching the
machine-wide `C:\Program Files\dotnet` install used by the 8/9/10 SDKs also on
this machine). `global.json` alone won't make a bare `dotnet` command on PATH
find it — either prepend that directory for the session
(`PATH="/c/Users/<you>/AppData/Local/Microsoft/dotnet:$PATH"` in Git Bash, or
the PowerShell equivalent) or invoke `dotnet.exe` there directly.

```
dotnet build                          # whole solution
dotnet test                           # whole solution — always do this after a slice lands, not just the touched project
dotnet test src/RhinoDB.Lib.Test      # one layer's suite while iterating
```

Three test projects mirror three production projects 1:1 — `RhinoDB.Core.Test`,
`RhinoDB.Lib.Test`, `RhinoDB.Native.Test` (suffix convention: `Foo.Test`, not
`Test.Foo`). Never let two stages go red at once — full-solution `dotnet test`
green is the gate before starting the next slice.

## Project layout

```
src/RhinoDB.Core     — pure contracts (Result/Option/DbError/exceptions), zero deps, no unsafe
src/RhinoDB.Lib      — the engine: Storage/, Indexing/, Tables/, Execution/ (DbContext, DbExecutionLoop)
src/RhinoDB.Native   — raw P/Invoke to vendored libmdbx; the ONLY project with AllowUnsafeBlocks
src/RhinoDB.Generators — Roslyn source generators (netstandard2.0), referenced as an Analyzer
src/RhinoDB.Run.Server — console host
src/*.Test           — one NUnit project per production project above
```

`RhinoDB.Native/vendor/libmdbx/` is vendored C source + `build.zig` (one script,
four platform targets). Read `vendor/libmdbx/VERSION.txt` before ever touching
it — explains the flat-file layout, the two source patches, and why CMake isn't
part of the day-to-day build path. Built binaries under `runtimes/{rid}/native/`
are committed; a normal clone/build/test never invokes Zig.

## Where decisions live — don't re-derive, don't duplicate

- `Docs/03-roadmap.md` — the stage tracker (`storage → indexes → transactions →
  libmdbx → propagation → networking → game`). Check it before starting new work;
  don't skip the dependency order even when a later stage looks more interesting.
- `Docs/02-architecture.md`, `Docs/01-performance-principles.md` — the "why"
  behind engine decisions (single-writer execution, no reflection anywhere,
  source generators over runtime dispatch).
- **The live Claude artifact is deprecated (2026-09-07) — don't fetch or cite
  it.** `Docs/*.md` used to be a summary pointing at a fuller Claude artifact for
  implementation-level detail; that's been inverted. The artifact's still-valid
  content was folded into `Docs/*.md` (capacity declarations, embedded-vs-
  networked consistency, cold-storage migration, the `Evict`/`Optimistic`-commit
  race fix, the B-tree variant reference); its transaction/execution pseudocode
  was deliberately *not* migrated because the real `DbContext`/`DbExecutionLoop`
  code and `Docs/02-architecture.md` already supersede it. `Docs/*.md` plus
  actual source under `src/` is now the complete picture — if a memory snapshot
  or old context still points at the artifact URL, treat that as stale, not as a
  reason to re-fetch it.
- Memory (`project-rhinodb-design` and siblings) — point-in-time architecture
  snapshots. Verify against current code before asserting a claimed shape/API
  still exists; memories say so explicitly and can go stale fast on a
  fast-moving repo like this one.
- A `/plans/*.md` file may exist for the stage currently in flight (check
  before assuming no plan exists) — it's the authoritative task breakdown for
  that stage, more detailed than the roadmap's one-paragraph summary.

## Known, intentional gaps — don't "fix" these without asking

- **Secondary-index uniqueness is in-memory-only; nothing enforces it against
  libmdbx.** Cold storage is keyed purely by primary key — no secondary-index
  KV pairs exist there (that's deferred future work, the "relational layer
  over libmdbx"). Any row not currently loaded (evicted, or never reloaded
  after restart) sits outside every unique secondary index's enforcement
  scope while its durable copy stays intact; a later `Insert` reusing that
  same "unique" value isn't caught by anything and silently produces a
  durable duplicate. This is a deliberate consequence of already-made
  decisions (no cold-storage probing on `Insert`/`Evict`), not a bug — a
  caller must never evict a row governed by a unique secondary index unless
  it can independently guarantee that value won't be reintroduced, since
  there is currently no library-provided way to check cold storage by
  secondary key at all. Full detail: `Docs/02-architecture.md` § Cold storage.

## Git commit style observed in this repo

`[tag] short description`, lowercase tag, imperative-ish: `[add]`, `[refactor]`,
`[doc]`, `[test]`, `[fix]`, `[cleanup]`. Match it rather than inventing a new style.

## Small habits that save a round trip

- When a design tangent comes up mid-task ("what if X instead of Y"), give the
  tradeoff grounded in the actual code (read the relevant file first) rather
  than reasoning from the architecture doc alone — the repo moves fast enough
  that docs/memory lag behind.
- Future/speculative ideas the user explicitly wants kept informal (not yet in
  the roadmap) belong in memory as a `project` entry, not as an unrequested
  roadmap edit — only add to `Docs/03-roadmap.md` when asked to queue/schedule it.
