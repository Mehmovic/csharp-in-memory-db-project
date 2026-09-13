# `src/RhinoDB.Lib/Execution/DbExecutionLoop.cs` — dev notes

## Each `Enqueue` overload — `txCreated` flag before calling `tx.Discard()`

`Discard()` (2026-09-13, added alongside making generated `Ops`/`Transaction`
instances long-lived per database instead of freshly constructed every
`Run` call) resets a table's staged-but-unapplied `changes`/`Dirty` state.
It must run whenever an operation ends in error, since `Apply()`'s own
`changes.Clear()` only happens on the success path, and the transaction
object is no longer discarded after each call the way it used to be - a
long-lived `Ops` instance that never gets its stale staged changes cleared
would leak them into the *next* operation that reuses it.

`CreateTransaction()` is still called inside the `try` (not hoisted above
it) even though the generated override is now just `=> cachedTransaction;`
(a field read that can't realistically throw) - the `txCreated` flag
preserves the exact original exception-safety contract regardless: if
`CreateTransaction()` itself somehow threw, `Discard()` is correctly skipped
(there's nothing to discard) and the exception is still caught and reported
as a normal `Result.Error`, exactly as before this change, rather than
tearing down `RunLoop`'s task.
