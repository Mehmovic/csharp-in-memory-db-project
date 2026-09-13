# `src/RhinoDB.Core/Results/IResult.cs` — dev notes

Added 2026-09-14, the one piece of the pooled-`ValueTask` execution-engine
redesign that touches `RhinoDB.Core` (otherwise "pure contracts, zero deps").
Scoped deliberately minimal: a self-referencing generic interface (`TSelf :
struct, IResult<TSelf>`, the same CRTP-style shape as `IEquatable<T>`) with
one instance member (`IsOk()`, already existed on both `Result`/`Result<T>`)
and two static-abstract members (`FromError`/`FromException`, thin wrappers
over each type's existing `Error(DbError)`/`Error(Exception)` statics - no
new behavior, purely additive).

Exists so `PooledOperation<TTx,TValue,TArgs>`
(`Docs/Dev/RhinoDB.Lib/Execution/PooledOperation.md`) can be ONE generic type
covering both `Run`'s `Result`-only and `Result<T>` overload shapes, instead
of two separate pooled types or a `Unit` sentinel. Confirmed boxing-free and
AOT-safe before building on it - see `Docs/01-performance-principles.md` §
"No closures on the hot path" for the full reasoning (instance calls on a
`where T : struct, IResult<T>` constrained parameter compile to
`constrained.callvirt`, JIT-specialized per value type; static-abstract
members resolve to a direct static call per closed generic type - the same
mechanism `INumber<T>` uses, and confirmed AOT-safe for the same reason
generic math is).

`Result`/`Result<T>` implement `FromError`/`FromException` **explicitly**
(`static Result IResult<Result>.FromError(DbError error) => Error(error);`)
rather than as ordinary public statics - deliberate, not an oversight: these
are meant to be reached only through the generic constraint (`TValue
.FromError(...)` inside `PooledOperation`), not as a second, redundant public
entry point alongside `Result.Error(...)` that would just add API surface
for no reason.
