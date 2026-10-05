# `src/RhinoDB.Lib/Execution/PooledOperation.cs` — dev notes

Added 2026-09-14, replacing `DbExecutionLoop`'s four duplicated
`TaskCompletionSource`+closure-per-call `Enqueue` bodies with one pooled,
reused mechanism. Full design/measurement writeup:
`Docs/01-performance-principles.md` § "No closures on the hot path",
`Docs/03-roadmap.md`'s 2026-09-14 entry. This file's notes are about the
implementation specifics those docs don't spell out line-by-line.

## Why one generic type covers all four `Enqueue` shapes

`TValue : struct, IResult<TValue>` (see `Docs/Dev/RhinoDB.Core/Results/IResult.md`)
lets `Result` and `Result<T>` share one pooled type instead of needing two -
`TValue.FromError(...)`/`TValue.FromException(...)` are static-abstract calls,
resolved per closed generic type at JIT time, no boxing, no virtual dispatch.

## `static readonly ConcurrentQueue<PooledOperation<TTx,TValue,TArgs>> pool`

One pool *per closed generic instantiation* - the JIT already buckets static
fields per closed generic type for free, so this needs no external
keyed-by-Type registry or hashing. `ConcurrentQueue<T>` chosen over a
hand-rolled lock-free stack per this project's own "don't optimize blind"
gate (same one already applied to the `ArrayPool`-for-`changes` idea and the
CSB+-tree backlog entry) - nothing has shown `ConcurrentQueue` insufficient.

## `Enqueue` — token captured *before* `channel.Writer.TryWrite`

```csharp
var token = item.core.Version;
if (!channel.Writer.TryWrite(item)) item.core.SetResult(TValue.FromError(DbError.ProcedureCreationFailed()));
return new ValueTask<TValue>(item, token);
```

Order matters and is not incidental: writing to the channel *before*
capturing the token would risk the reader thread completing the item,
`GetResult` recycling it to an unrelated concurrent caller, and *then* this
producer reading a now-stale version - a real, if narrow, cross-contamination
race. Capturing first closes it. Stress-tested directly by
`PooledOperationTests.ConcurrentEnqueueAndConsume_UnderMultipleThreads_EachCallerGetsItsOwnCorrectValue`
(8 producers × 100 operations, each asserting it gets back its own value).

## `IExecutionWorkItem` is not generic over `TTx`, and `Run()` takes no parameter

First built with `Run(DbContext<TTx> context)` and `IExecutionWorkItem<TTx>`,
mirroring `RunLoop`'s call site (`item.Run(context)`). Caught in review: since
`PooledOperation` already stores its own `context` field (set in `Enqueue`,
read inside `Run`), and `DbExecutionLoop<TTx>` only ever has *one* `DbContext<TTx>`
for its entire lifetime, the parameter passed to `Run` could never differ
from the field - pure redundancy, not a second, independent piece of state.
Simplified to `Run()` (reads `context!` internally) and, since nothing in the
interface's members reference `TTx` anymore, `IExecutionWorkItem` itself
dropped its `<TTx>` type parameter entirely (a genuinely unused generic
parameter the compiler flags on its own) - `DbExecutionLoop<TTx>`'s channel
is now `Channel<IExecutionWorkItem>`, not `Channel<IExecutionWorkItem<TTx>>`.

## `Run` — reproduces `DbExecutionLoop`'s old per-overload closure body verbatim

`BeginScope` → `CreateTransaction()` → `txCreated` flag → try/catch → `Apply()`
→ `Discard()`-on-error → `EndScope` → `Complete`/`Finalize`, all unchanged in
substance from what used to be duplicated four times in `DbExecutionLoop.cs`
- collapsing four copies into one generic method was a real risk of
"simplifying" the `txCreated` guard away by accident; it wasn't, and is
reproduced exactly (see `Docs/Dev/RhinoDB.Lib/Execution/DbExecutionLoop.md`
for why the flag exists at all).

One necessary difference from the original: `applyResult.IsError() ? result
= applyResult` relied on `Result<T>`'s implicit `operator Result<T>(Result)`
conversion, which can't resolve generically over `TValue`. Replaced with the
equivalent explicit `TValue.FromError(applyResult.GetError())` - same
resulting value, spelled out since the operator overload isn't visible to
generic code.

## `GetResult` — return-to-pool ordering

```csharp
public TValue GetResult(short token) {
    var result = core.GetResult(token);   // throws here first if token is stale/wrong - see below
    context = null;
    operation = null;
    args = default!;
    core.Reset();
    pool.Enqueue(this);
    return result;
}
```

`core.GetResult(token)` runs *first*, before touching any of this instance's
own state or the pool. If the token doesn't match (double consumption, or a
consumer holding a stale `ValueTask` after the instance was already recycled
to someone else), `GetResult` throws immediately and nothing below it runs -
the instance is never incorrectly nulled-out or double-returned to the pool.
Proven directly by
`PooledOperationTests.AfterConsumption_ASecondConsumptionOfTheSameValueTaskThrows`.

`context`/`operation` are nulled *before* returning to the pool, not left for
the next rent to overwrite - an idle pooled instance must not keep the
previous operation's `DbContext`/delegate (and whatever they in turn root)
alive between uses. Proven directly by
`PooledOperationTests.AfterConsumption_ContextAndOperationReferencesAreNulledOutBeforeReturningToThePool`.

## Real, non-obvious `ValueTask` behavioral gotchas hit while wiring this in (not bugs in this file - contract differences from `Task` every caller needs to know)

- `ValueTask<T>` is single-consumption - see the double-consumption test
  above. `Task<T>` supports being awaited/inspected any number of times;
  `ValueTask<T>` backed by a custom `IValueTaskSource<T>` does not.
- `ValueTask<T>` does **not** support a blocking synchronous wait the way
  `Task<T>.GetAwaiter().GetResult()` does. `ManualResetValueTaskSourceCore<T>
  .GetResult` assumes proper `await`/`OnCompleted` coordination already ran,
  and throws `InvalidOperationException` if called before the operation
  actually completed - it does not block-and-wait like `Task` does. This
  broke three benchmark `[GlobalSetup]` methods that did
  `db.Run(...).GetAwaiter().GetResult();` synchronously right after
  enqueueing (invisible to the whole test suite, since every test either used
  real `await` or drove completion before consuming) - fixed there by
  inserting `.AsTask()` before the blocking wait. Any RhinoDB-consuming code
  needing a genuine synchronous block on a `Run` result must do the same.
