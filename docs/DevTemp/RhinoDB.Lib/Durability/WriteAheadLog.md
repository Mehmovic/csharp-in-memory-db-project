# `src/RhinoDB.Lib/Durability/WriteAheadLog.cs` — dev notes

Group commit engine, `Docs/05-wal-design.md` Phase 2. `Confirmed` appends join whatever
fsync is already in flight, or trigger a new one — no `Task.Delay`/window anywhere (the
exact mechanism the 2026-09-13 coalescing-window experiment failed to achieve on
libmdbx, see `Docs/Dev/RhinoDB.Lib/Cold/ColdStore.md`). `Optimistic` appends never wait;
their durability rides the next flush, triggered by a `Confirmed` arrival, the
size-threshold hard cap (doc risk #4 — a bound, not a soft hint), or the periodic tick —
whichever fires first.

## `TestOnlyBeforeFlush`

Test-only seam: a real flush is too fast to reliably race two `Confirmed` calls against
(no `Task.Delay`/window exists to hold a group open deterministically, by design). Lets
`WriteAheadLogTests.cs` hold `RunFlush` inside its critical section long enough to prove
a second concurrent append actually joins the in-flight group rather than starting a
new one.
