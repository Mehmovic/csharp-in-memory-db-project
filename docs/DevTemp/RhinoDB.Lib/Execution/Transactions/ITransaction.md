# `src/RhinoDB.Lib/Execution/Transactions/ITransaction.cs` — dev notes

## `Discard()`

Added 2026-09-13 alongside making generated `Ops`/`Transaction` instances
long-lived per database (see
`Docs/Dev/RhinoDB.Generators/TableGenerator.md` § long-lived `Ops`/
`Transaction` instances). Resets a transaction's staged-but-unapplied
changes across every table - needed because `Apply()`'s own cleanup
(`changes.Clear()`/`Dirty = false`) only runs on the success path, and a
long-lived transaction object that never gets cleared after a *failed*
operation would leak that operation's staged changes into whichever future
operation reuses it next. `DbExecutionLoop` calls it whenever an operation's
`Result` is an error, regardless of the reason (failed `Validate()`, a
thrown exception, or the operation delegate itself returning an error
without ever calling `Apply()`).
