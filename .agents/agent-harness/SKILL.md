---
name: agent-harness
description: >
  Use when changing the agent loop pipeline: AgentHarness coordination (turn
  orchestration, session/model selection, phase tracking, steering/follow-up
  queue, tool registration, system prompt composition, persistence, extension
  events, compaction), the durability-first pipeline stages, or event dispatch
  to extensions. Covers the loop lifecycle and the synchronous-extension-hook
  constraint.
type: domain
scope:
  - src/PiSharp.Agent/Harness/**
  - src/PiSharp.Agent/Loops/**
  - src/PiSharp.Runtime/**
  - docs/pisharp-developer-guide.md
related_skills:
  - sessions-and-persistence
  - extension-platform
  - tools-and-commands
  - model-providers
last_verified:
  commit: "646522ccc6edc48acc39e4545cd120af9f1dafba"
  date: "2026-08-14"
confidence: high
---

# Agent Harness and Event Pipeline

## When to use this skill

Use this skill when:

- changing the agent loop or pipeline stages;
- changing turn orchestration (session/model selection, phase tracking);
- changing steering/follow-up queue behavior;
- changing system prompt composition;
- adding agent event types dispatched to extensions;
- changing compaction triggers or persistence timing;
- changing tool registration in the harness.

Typical tasks include:

- reordering pipeline stages;
- adding a pipeline stage;
- changing when persistence happens in a turn;
- adding a new agent event type.

Do not use this skill for:

- session file format — use [sessions-and-persistence](../sessions-and-persistence/SKILL.md);
- extension API surface — use [extension-platform](../extension-platform/SKILL.md);
- built-in tool implementations — use [tools-and-commands](../tools-and-commands/SKILL.md);
- provider/model selection internals — use [model-providers](../model-providers/SKILL.md).

## Responsibilities and boundaries

This area owns:

- the per-session agent loop (`AgentHarness<TMetadata>`);
- pipeline stage ordering and semantics;
- turn/phase lifecycle;
- system prompt composition;
- steering/follow-up queue;
- persistence timing and compaction coordination.

This area does not own:

- the session storage format (sessions-and-persistence);
- extension lifecycle (extension-platform);
- individual tool implementations (tools-and-commands).

## Architecture

`AgentHarness<TMetadata>` coordinates each session's turns: session/model
selection, phase tracking, a steering/follow-up queue, tool registration,
system prompt composition, persistence, extension event dispatch, and
compaction.

The loop is durability-first; the pipeline runs:

1. `PersistenceStage` — persist turn state before any side effects are
   dispatched.
2. `PhaseTransitionStage` — apply phase transitions.
3. `ToolMiddlewareStage` — run tool middleware.
4. `ExtensionDispatchStage` — dispatch agent events to extensions.
5. `ListenerNotificationStage` — notify listeners (hot consumers kept off the
   critical path).

Mutating extension hooks run synchronously; hot consumers are kept off the
critical path.

### Important components

| Component | Location | Responsibility |
|---|---|---|
| Harness | `src/PiSharp.Agent/Harness/AgentHarness.cs` | Turn coordination |
| Loop pipeline | `src/PiSharp.Agent/Loops/` | Durability-first stages |
| Runtime composition | `src/PiSharp.Runtime/SessionRuntime.cs` | Harness factory wiring |
| Session repo | `src/PiSharp.Agent/Sessions` | Persistence backing (sessions-and-persistence) |

### Main flow

1. `SessionRuntime` builds a harness via the harness factory.
2. A turn starts: session/model selection, system prompt composition.
3. Pipeline stages run in durability-first order; persistence lands first.
4. Tool middleware runs; tools execute (tools-and-commands).
5. Extension dispatch emits agent events to registered extensions.
6. Listener notification follows; compaction may be triggered.

## Project terminology

| Term | Meaning in this repository |
|---|---|
| Harness | `AgentHarness<TMetadata>` per-session coordinator |
| Turn | One agent iteration (prompt -> completion -> tools -> next) |
| Phase | Lifecycle phase tracked per turn (e.g. executing, waiting, aborted) |
| Steering/follow-up queue | Queued user directions for the next turn |
| Durability-first | Persist before dispatch so a crash never loses state |
| ExtensionDispatchStage | Stage that emits agent events to extensions |

## Important entry points
- [`skills/SKILL.md`](../../SKILL.md): project router — routing index for all PiSharp project skills.


- [`src/PiSharp.Agent/Harness/AgentHarness.cs`](../../../src/PiSharp.Agent/Harness/AgentHarness.cs)
- [`src/PiSharp.Agent/Loops/`](../../../src/PiSharp.Agent/Loops/)
- [`docs/pisharp-developer-guide.md`](../../../docs/pisharp-developer-guide.md)
  (harness + pipeline section)

## Dependencies and consumers

### Depends on

- `src/PiSharp.Agent` (sessions), `src/PiSharp.Agent.Core`/`Abstractions`
  (contracts), `src/PiSharp.Extensions` (extension events).

### Consumed by

- `SessionRuntime`, the daemon (warm runtimes), TUI/CLI (session control).

### External systems

- None directly; providers and tools are invoked through their own layers.

## Invariants

The following must remain true:

1. Pipeline order is durability-first: persistence precedes dispatch.
2. Persistence happens before extension dispatch — a crash after dispatch must
   not lose the turn.
3. Mutating extension hooks run synchronously; hot consumers (listeners) stay
   off the critical path.
4. Compaction must preserve resumability (see
   [sessions-and-persistence](../sessions-and-persistence/SKILL.md)).
5. Model selection and system prompt composition stay in the harness — not in
   views or the daemon.

## Common change workflows

### Add a pipeline stage

1. Implement the stage following the existing stage pattern in
   `src/PiSharp.Agent/Loops/`.
2. Insert it at the correct position in the durability-first order.
3. Add a test asserting stage order and side-effect timing.

Files commonly changed together:

- `src/PiSharp.Agent/Loops/**`
- `tests/PiSharp.Agent.Tests/**`

Validation:

```bash
dotnet build PiSharp.sln
dotnet test tests/PiSharp.Agent.Tests/PiSharp.Agent.Tests.csproj
```

### Add an agent event type

1. Add the event type to the harness event surface.
2. Dispatch it in `ExtensionDispatchStage` (or the relevant stage).
3. If extensions need it over TsBridge, wire parity layers (see
   [tsbridge-parity](../tsbridge-parity/SKILL.md)).

Files commonly changed together:

- `src/PiSharp.Agent/Harness/**`
- `src/PiSharp.Extensions/**` (event constants, see extension-platform)
- `src/PiSharp.TsBridge/**` (if TS-visible)

Validation:

```bash
dotnet test tests/PiSharp.Agent.Tests/PiSharp.Agent.Tests.csproj
dotnet test tests/PiSharp.TsBridge.Tests/PiSharp.TsBridge.Tests.csproj
```

## Testing and validation

Run for all changes in this area:

```bash
dotnet build PiSharp.sln
dotnet test tests/PiSharp.Agent.Tests/PiSharp.Agent.Tests.csproj
```

Run conditionally:

```bash
dotnet test PiSharp.sln
```

## Operational considerations

- Pipeline stage ordering is load-bearing for durability; changes need explicit
  order tests.
- Extension dispatch is a synchronization point — long-running extension hooks
  block the loop; keep hot consumers in the listener stage.

## Common mistakes

- Do not move persistence after dispatch — that breaks the durability-first
  guarantee.
- Do not run mutating extension hooks asynchronously; they must be synchronous
  to preserve ordering guarantees.
- Do not put model selection or system-prompt logic into views or the daemon.
- Do not add listener work to the critical path.

## Legacy and deprecated patterns

- Earlier in-process-only designs predate the daemon; the harness must stay
  transport-agnostic (works in `--local` and daemon-hosted modes).

## Existing authoritative documentation

- [`docs/pisharp-developer-guide.md`](../../../docs/pisharp-developer-guide.md)

  * Covers the harness, pipeline stages, and durability-first ordering.
  * Treat as authoritative for the pipeline.

## Known ambiguity and technical debt

- Pipeline stage names/order may evolve; re-read `src/PiSharp.Agent/Loops/`
  when the guide and code disagree.
- Compaction interaction with the steering/follow-up queue is subtle; test
  compact-then-steer scenarios.

## Evidence and verification

This skill was verified against commit `646522ccc6edc48acc39e4545cd120af9f1dafba`.

Primary evidence:

- [`src/PiSharp.Agent/Harness/AgentHarness.cs`](../../../src/PiSharp.Agent/Harness/AgentHarness.cs)
- [`src/PiSharp.Agent/Loops/`](../../../src/PiSharp.Agent/Loops/)
- [`src/PiSharp.Runtime/SessionRuntime.cs`](../../../src/PiSharp.Runtime/Runtime/SessionRuntime.cs)
- [`docs/pisharp-developer-guide.md`](../../../docs/pisharp-developer-guide.md)
