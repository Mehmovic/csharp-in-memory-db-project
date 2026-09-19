# Client codegen — exporting tables, types, and RPCs to C# and TypeScript clients

Status: design agreed 2026-09-20, nothing in `src/` implements any of this yet.
Stage 6/7/8-adjacent (needs [Networking](04-networking.md)'s real-time layer and
a real RPC surface to be worth building) - recorded now so the eventual
implementation starts from an agreed contract instead of a blank page, the same
reason [Schema migration](06-schema-migration.md) was written down early.

## 1. The problem

Once a developer has defined databases, tables, and CustomTypes for their own
server (see [Architecture](02-architecture.md)), that same schema needs to
reach two different kinds of client:

- **A C# client** (e.g. a Unity game client) — same runtime as the server, but
  should not need to reference the server's own assembly (`RhinoDB.Lib`,
  `ColdStore`, the WAL, generated `Ops`/`Database` classes) just to talk to it.
- **A TypeScript client** (a web app, later) — a completely different runtime
  (V8/Node), which cannot load a .NET assembly under any circumstance. This
  isn't a "not built yet" limitation, it's a hard boundary between runtimes.

Both need: the row/type shapes, and working (de)serialization code for
whichever wire format the deployment actually uses. This doc is about how that
gets generated and exported, not about the RPC transport itself (that's
[Networking](04-networking.md)'s job).

## 2. The three pipelines, and which ones ever leave the server (recap)

Full detail in [Architecture — three serialization pipelines](02-architecture.md#three-serialization-pipelines-not-one).
The two facts this doc builds on:

- **Durability (Raw/WAL/cold storage) is always MemoryPack's hand-rolled
  positional format - fixed, never configurable, never exported.** It never
  reaches a client of any kind; it's purely internal.
- **IDC and Client are each an independent, single, fixed protocol choice per
  deployment** - not per-request, not negotiable at runtime, not swappable
  after the fact. IDC picks one of {plain MemoryPack, VersionedMemoryPack}.
  Client picks one of {VersionedMemoryPack, MessagePack}. This doc is only
  about the **Client** choice - IDC bytes never leave the set of `DbContext`s
  an operator controls, so no external client-codegen story applies to it.

**A fact that isn't a design choice, just a fact**: MemoryPack has no
non-.NET implementation, anywhere, permanently. `VersionedMemoryPack` can
therefore only ever be a C#-to-C# protocol. MessagePack has real
implementations in most languages (including JS/TS -
[`@msgpack/msgpack`](https://www.npmjs.com/package/@msgpack/msgpack)), so it's
the **only** Client protocol choice that can ever reach a non-C# client. This
is exactly why Client keeps both options instead of consolidating to one -
MessagePack isn't "the other option" for a non-C# client, it's the *only* one.

## 3. The decision: one uniform codegen tool, not a special-cased C# path

Considered and rejected: ship a precompiled, trimmed "contracts" DLL for C#
clients (compiled once at the server's build time, containing just the row/
type POCOs and their already-generated formatters), paired with a separate
TypeScript-only generator for non-C# clients. Technically workable, but it
means two different mechanisms for what is conceptually one problem, and it's
the one place in this whole design that would reintroduce the thing everything
else here deliberately avoids: a binary artifact the client can't inspect,
diff, or trim. Every other generated-code decision in this project (the raw
serializer, the mandatory `[MemoryPackable]`/`[MessagePackObject]`/
`[CustomType]` attributes, the shorthand pre-build tool) has consistently
chosen real, readable, reviewable generated *source* over hidden magic - a
compiled DLL for C# clients only would be the one exception, for no reason
better than convenience.

**Decision**: build one codegen tool that walks the same field model
`TableGenerator`/`CustomTypeGenerator` already build internally
(`RowFieldModel`, `TableModel`, `CustomTypeModel` - see
[Architecture](02-architecture.md) and the ring-buffer/serialization work) and
emits real source for whichever target language is requested - C# is just
another codegen target, not a special case. This is the same shape
SpacetimeDB's `spacetime generate` takes (bindings generated per client
language from the module's schema), chosen deliberately for consistency with
this project's own established values, not merely because SpacetimeDB does it.

A precompiled DLL isn't ruled out forever - it could still be offered *later*
as an optional convenience layer for C# clients that don't want a codegen step
in their build - but it is not the primary mechanism, and building it first
would be designing the exception before the rule.

## 4. Per-language codegen behavior

### C# targets

Generate bindings for **both** `VersionedMemoryPack` and `MessagePack`
unconditionally, regardless of which one the deployment actually configured.
No compatibility check, no gatekeeping - C# can consume either format, so the
tool isn't the thing responsible for preventing a mismatch. That
responsibility sits with the developer: they must know and use the binding
that matches their server's actual configured Client protocol. A protocol
mismatch at the wire level (server sends MessagePack, client deserializes as
VersionedMemoryPack) is a hard runtime failure either way - no codegen-time
check can catch a developer wiring up the wrong generated binding by hand, so
there's nothing to validate at generation time for this target.

Follow-up not yet designed: how the two generated binding sets are named/
namespaced so a developer can tell them apart at a glance (e.g. separate
namespaces per protocol) - a real usability question, deferred until this
tool is actually built.

### TypeScript (and any future non-C# language) targets

**Must** check the deployment's configured Client protocol before generating
anything, and hard-error - loudly, clearly, no partial/best-effort output - if
it isn't `MessagePack`. There is no fallback to offer a non-C# client if the
deployment chose `VersionedMemoryPack`, so silently generating broken bindings
or guessing would be strictly worse than refusing. This is the same "fail
fast rather than silently degrade" discipline already used everywhere else in
this codebase (RHINO015/016/017's mandatory-attribute diagnostics, the
zero-reflection-fallback design of the Raw pipeline) - applied here at the
codegen boundary instead of compile time.

## 5. Prerequisite this depends on, not yet built

For the TypeScript check in §4 to be possible at all, the deployment's chosen
Client protocol needs to be a real, declared, machine-readable setting the
codegen tool can read - not a convention someone remembers. The natural shape,
mirroring the already-documented IDC declaration
(`[Database(Idc = WireFormat.MemoryPack)]`, see
[Architecture — Declarative surface](02-architecture.md#declarative-surface)):

```csharp
[Database(Idc = WireFormat.MemoryPack, ClientProtocol = ClientProtocol.MessagePack)]
public partial class GameDb : DbContext<GameDbTransaction> { }
```

Not designed further than this shape - the exact enum/attribute surface is a
detail for whenever Stage 8's Client pipeline is actually built. Recorded here
because it's a hard prerequisite for §4's TypeScript check, not an optional
nice-to-have: without a real declared setting, there is nothing for the
codegen tool to check against.

## 6. Explicit scope boundary

- This doc is about **what gets exported and how it's generated** - the field-
  model-to-source-code problem. It is not about the RPC/transport wire
  protocol itself (frames, sessions, delivery guarantees - see
  [Networking](04-networking.md)), and not about *invoking* RPCs from a
  generated client (that needs the real-time layer built first).
- Not designed here: how a generated client subscribes to table changes and
  applies diffs (Stage 8's subscribe-and-diff model, per
  [Networking](04-networking.md) and the per-table `ChangeRingBuffer` work) -
  this doc only covers getting the *shapes and (de)serialization code* to the
  client, which that subscription/diff machinery would then use.
- Not designed here: versioning/distribution mechanics for generated client
  packages (how a TypeScript `npm` package or a C# NuGet package gets built
  and published from a codegen run) - a real but separate concern from the
  codegen tool's actual input/output contract.
