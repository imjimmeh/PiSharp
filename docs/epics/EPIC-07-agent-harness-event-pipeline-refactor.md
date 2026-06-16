---
epic_id: EPIC-07
title: Agent Harness Loop Event Pipeline Refactor
status: proposed
priority: high
owner: unassigned
created: 2026-05-26
updated: 2026-05-26
target_version: backlog
related_docs:
  - ../specs/PRD-pi-csharp-port.md
  - ../specs/SDD-pi-csharp-port.md
  - ./EPIC-01-core-abstractions.md
  - ./EPIC-06-extension-system-and-ts-bridge.md
related_code:
  - ../../src/PiSharp.Agent/Harness/AgentHarness.cs
  - ../../src/PiSharp.Extensions/ExtensionRegistry.cs
  - ../../src/PiSharp.Extensions/ExtensionEvents.cs
  - ../../tests/PiSharp.Agent.Tests/Harness/AgentHarnessEventTests.cs
  - ../../tests/PiSharp.Extensions.Tests/ExtensionRegistryTests.cs
decision_summary: Adopt a hybrid mediator + strategy pipeline for loop event handling to improve SRP, testability, and extension-safe evolution.
tags:
  - architecture
  - refactor
  - agent-harness
  - events
  - extensions
---

# EPIC-07: Agent Harness Loop Event Pipeline Refactor

## 1. Background And Context

### 1.1 Current Implementation

The core event handling path for runtime loop events currently lives in a single method:

- `src/PiSharp.Agent/Harness/AgentHarness.cs` -> `HandleLoopEventAsync(AgentEvent e, CancellationToken cancellationToken)`

Current behavior in that method:

1. Performs persistence branching logic for `MessageEnd`, `TurnEnd`, and `AgentEnd`.
2. Performs phase mutation (`_phase = AgentHarnessPhase.Idle` on `AgentEnd`).
3. Wraps the core event into `AgentHarnessEvent.Core`.
4. Dispatches the event to extension handlers via `ExtensionRegistry.DispatchAsync`.
5. Notifies harness listeners via `NotifyHarnessEventAsync`.
6. Applies partial fault isolation for extension and listener failures.

### 1.2 Why This Is Risky Long-Term

The current method works, but it mixes multiple concerns in one branch-heavy procedure:

- Session persistence orchestration
- Harness lifecycle state transitions
- Extension integration
- Listener notification
- Error/cancellation policy boundaries

Potential issues as the codebase grows:

- Harder to introduce new event concerns without editing a fragile central method.
- Easier to accidentally change execution order and cause behavioral regressions.
- Hard to test concern-specific behavior in isolation (for example, "flush before dispatch" ordering).
- Reduced extension safety if new code paths bypass existing fault/cancellation guarantees.
- Violates SRP and makes OCP evolution harder: each new concern requires edits to the same method.

### 1.3 System-Level Importance

This path is high-leverage because it sits on the boundary between:

- agent runtime loop (`AgentEvent` stream)
- session durability (`AppendMessageAsync`, `AppendModelChangeAsync`, etc.)
- extension ecosystem (`ExtensionRegistry` and mapped extension events)
- application observers/listeners (`Subscribe` callbacks)

Any drift in ordering or error policy here can impact reliability, observability, and extension behavior.

## 2. Problem Statement

Refactor loop event handling so that:

1. Responsibilities are separated into explicit components.
2. Event processing order is intentional and test-locked.
3. Extension and listener fault-isolation remains preserved.
4. New event concerns can be added with minimal churn in `AgentHarness`.
5. The code aligns with SOLID and maintainability goals.

## 3. Goals And Non-Goals

### 3.1 Goals

- Introduce a composable event pipeline abstraction for loop event processing.
- Keep behavior parity for existing runtime semantics.
- Keep cancellation behavior parity.
- Improve testability through unit tests focused on stage behavior and ordering.
- Minimize risk through PR-sized incremental migration.

### 3.2 Non-Goals

- No redesign of `AgentEvent` union types.
- No change to extension event naming contract in `ExtensionEventMapper`.
- No change to session storage model or JSON serialization formats.
- No broad rewrite of `AgentLoop` implementation.

## 4. Options Considered

### Option A: Keep Current Method, Add More Comments/Regions

Description:

- Keep all logic in `HandleLoopEventAsync` and improve readability with comments.

Pros:

- Lowest immediate engineering effort.
- No additional abstractions.

Cons:

- Does not solve SRP/OCP pressure.
- Testability and long-term maintainability remain weak.
- Future extension hooks still require risky edits in a central method.

Decision:

- Rejected.

### Option B: Extract Private Methods Inside AgentHarness Only

Description:

- Split each concern into private methods but keep orchestration and branching tightly bound to `AgentHarness`.

Pros:

- Better readability than current implementation.
- Smallest refactor surface among structural options.

Cons:

- Still tightly coupled to `AgentHarness` state and sequencing.
- Limited composability and limited independent test seams.
- New concerns still require touching the same orchestration code path.

Decision:

- Rejected as an intermediate improvement only.

### Option C: Hybrid Mediator + Strategy Pipeline (Chosen)

Description:

- Add a `LoopEventPipeline` coordinator (mediator-style orchestration).
- Add ordered `ILoopEventStage` handlers (strategy components) for each concern.
- Keep `AgentHarness` as composition root for the pipeline.

Pros:

- Strong SRP separation per stage.
- Explicit, testable ordering contract.
- Easier to add/replace behavior with minimal core edits.
- Better isolation for extension evolution.

Cons:

- Introduces additional types/files.
- Requires migration coordination and new tests.

Decision:

- Accepted.

### Option D: Full Cross-Cutting Event Bus Framework

Description:

- Replace direct handling with generalized pub/sub event bus across runtime.

Pros:

- Highly extensible for future platform-level plugins.

Cons:

- Over-engineering for current scope.
- Significant complexity and migration risk.
- Unclear value compared to pipeline approach at this stage.

Decision:

- Rejected for now.

## 5. Chosen Design

### 5.1 Core Shape

Introduce the following components under a new folder:

- `src/PiSharp.Agent/Harness/LoopEvents/ILoopEventStage.cs`
- `src/PiSharp.Agent/Harness/LoopEvents/HarnessLoopEventContext.cs`
- `src/PiSharp.Agent/Harness/LoopEvents/LoopEventPipeline.cs`
- `src/PiSharp.Agent/Harness/LoopEvents/PersistenceStage.cs`
- `src/PiSharp.Agent/Harness/LoopEvents/PhaseTransitionStage.cs`
- `src/PiSharp.Agent/Harness/LoopEvents/ExtensionDispatchStage.cs`
- `src/PiSharp.Agent/Harness/LoopEvents/ListenerNotificationStage.cs`

### 5.2 Stage Ordering Contract

Execution order for each loop event:

1. Persistence stage
2. Phase transition stage
3. Extension dispatch stage
4. Listener notification stage

Rationale:

- Persist first to preserve durability expectations before external side effects.
- Apply lifecycle transition before external notifications where relevant.
- Dispatch to extensions before listener notifications to preserve current semantics.
- Keep listener notifications last as the final observer phase.

### 5.3 Error And Cancellation Policy

- `OperationCanceledException` should be rethrown when cancellation is requested.
- Non-cancellation exceptions in extension dispatch should be swallowed (current behavior parity).
- Non-cancellation exceptions in listener callbacks should be swallowed per listener (current behavior parity).

### 5.4 Compatibility

- No public API changes intended for `AgentHarness<TMetadata>` consumers.
- Existing extension event names and mappings remain unchanged.
- Existing event payload contracts remain unchanged.

## 6. PR-Sized Task Breakdown

## PR-1: Introduce Pipeline Contracts And Baseline Wiring

### Overview

Create the loop-event pipeline abstractions and wire `AgentHarness` to call the pipeline while preserving current behavior.

### Files In Scope

- `src/PiSharp.Agent/Harness/AgentHarness.cs`
- `src/PiSharp.Agent/Harness/LoopEvents/ILoopEventStage.cs` (new)
- `src/PiSharp.Agent/Harness/LoopEvents/HarnessLoopEventContext.cs` (new)
- `src/PiSharp.Agent/Harness/LoopEvents/LoopEventPipeline.cs` (new)

### Acceptance Criteria / Definition Of Done

- [ ] New pipeline contracts compile and are internal to `PiSharp.Agent` unless broader visibility is needed.
- [ ] `HandleLoopEventAsync` delegates to pipeline entrypoint.
- [ ] Behavior is unchanged from user perspective.
- [ ] No extension mapping or naming behavior changes.
- [ ] Code comments explain any non-obvious ordering rationale.

### Testing Criteria

- [ ] Existing harness and extension tests remain green.
- [ ] Add at least one smoke-level test validating pipeline invocation path is active.

## PR-2: Extract Persistence Stage

### Overview

Move persistence-related branching from `HandleLoopEventAsync` into `PersistenceStage`.

### Files In Scope

- `src/PiSharp.Agent/Harness/AgentHarness.cs`
- `src/PiSharp.Agent/Harness/LoopEvents/PersistenceStage.cs` (new)
- `tests/PiSharp.Agent.Tests/Harness/AgentHarnessEventTests.cs`
- `tests/PiSharp.Agent.Tests/Harness/AgentHarnessTests.cs`

### Acceptance Criteria / Definition Of Done

- [ ] `MessageEnd` queues/appends messages exactly as before.
- [ ] `TurnEnd` flushes pending writes exactly as before.
- [ ] `AgentEnd` flushes pending writes exactly as before.
- [ ] No duplicate writes or reordered write side effects.

### Testing Criteria

- [ ] Add focused tests for `MessageEnd`, `TurnEnd`, `AgentEnd` persistence behavior.
- [ ] Add test for idempotent/expected behavior when pending write queue is empty.

## PR-3: Extract Phase Transition Stage

### Overview

Move lifecycle phase mutation (notably `AgentEnd -> Idle`) into dedicated stage.

### Files In Scope

- `src/PiSharp.Agent/Harness/AgentHarness.cs`
- `src/PiSharp.Agent/Harness/LoopEvents/PhaseTransitionStage.cs` (new)
- `tests/PiSharp.Agent.Tests/Harness/AgentHarnessEventTests.cs`

### Acceptance Criteria / Definition Of Done

- [ ] `AgentEnd` transitions harness phase to idle at the correct point in stage order.
- [ ] No regressions in `PromptAsync`, `WaitForIdleAsync`, or run completion semantics.

### Testing Criteria

- [ ] Add test for phase transition timing relative to flush/dispatched events.
- [ ] Verify no deadlock/regression in async run completion path.

## PR-4: Extract Extension Dispatch Stage

### Overview

Move extension dispatch logic and its fault-isolation behavior into `ExtensionDispatchStage`.

### Files In Scope

- `src/PiSharp.Agent/Harness/AgentHarness.cs`
- `src/PiSharp.Agent/Harness/LoopEvents/ExtensionDispatchStage.cs` (new)
- `src/PiSharp.Extensions/ExtensionRegistry.cs` (if minimal helper adjustments are needed)
- `tests/PiSharp.Extensions.Tests/ExtensionRegistryTests.cs`
- `tests/PiSharp.Agent.Tests/Harness/AgentHarnessEventTests.cs`

### Acceptance Criteria / Definition Of Done

- [ ] Existing `ExtensionRegistry.DispatchAsync` contract remains unchanged.
- [ ] Cancellation behavior parity is preserved.
- [ ] Non-cancellation extension exceptions do not break mode execution.

### Testing Criteria

- [ ] Add tests for cancellation propagation.
- [ ] Add tests for extension exception isolation.
- [ ] Verify event name mapping compatibility still passes.

## PR-5: Extract Listener Notification Stage

### Overview

Move listener notification logic into `ListenerNotificationStage`, keeping per-listener fault isolation.

### Files In Scope

- `src/PiSharp.Agent/Harness/AgentHarness.cs`
- `src/PiSharp.Agent/Harness/LoopEvents/ListenerNotificationStage.cs` (new)
- `tests/PiSharp.Agent.Tests/Harness/AgentHarnessEventTests.cs`

### Acceptance Criteria / Definition Of Done

- [ ] All subscribed listeners still receive core events.
- [ ] Listener failures remain isolated and do not prevent subsequent listeners.
- [ ] Cancellation behavior parity is preserved.

### Testing Criteria

- [ ] Add tests for multi-listener sequencing and isolation.
- [ ] Add test for cancellation-aware listener exception behavior.

## PR-6: Lock Ordering Contract And Remove Legacy Branching

### Overview

Finalize by asserting pipeline ordering via dedicated tests and remove obsolete branching code from `HandleLoopEventAsync`.

### Files In Scope

- `src/PiSharp.Agent/Harness/AgentHarness.cs`
- `src/PiSharp.Agent/Harness/LoopEvents/LoopEventPipeline.cs`
- `tests/PiSharp.Agent.Tests/Harness/AgentHarnessEventTests.cs`
- `tests/PiSharp.Agent.Tests/Harness/AgentHarnessPipelineTests.cs` (new)

### Acceptance Criteria / Definition Of Done

- [ ] `HandleLoopEventAsync` is reduced to pipeline delegation and thin context assembly.
- [ ] Stage order is encoded in tests and documented in code.
- [ ] No duplicated event handling logic remains.

### Testing Criteria

- [ ] Add explicit ordering test: persistence -> phase -> extensions -> listeners.
- [ ] Ensure targeted test suite for harness/event flow is green.
- [ ] Run a broader regression pass for agent + extensions test projects.

## 7. Dependencies And Risks

### 7.1 Dependencies

- Stability of `AgentEvent` and `AgentHarnessEvent` unions.
- Existing extension event mapping in `ExtensionEventMapper`.
- Existing session write semantics in `ISession<TMetadata>` implementations.

### 7.2 Key Risks

- Behavioral drift due to ordering changes.
- Hidden coupling between phase transitions and listener expectations.
- Over-exposure of `AgentHarness` internals to stage types.

### 7.3 Risk Mitigations

- Preserve strict stage order and test it directly.
- Keep stage interfaces narrow and internal.
- Use incremental PR slices with green tests at each step.
- Favor behavior parity over opportunistic cleanup during migration.

## 8. Rollout And Validation Plan

1. Land PRs sequentially in small slices.
2. Run targeted test suites after each PR:
   - `PiSharp.Agent.Tests`
   - `PiSharp.Extensions.Tests`
3. Run full solution tests before epic closure.
4. Verify no extension event compatibility regressions.

## 9. Epic-Level Definition Of Done

- [ ] All PR tasks completed and merged.
- [ ] Stage-based pipeline in place with explicit ordering tests.
- [ ] No behavioral regressions in harness event flow.
- [ ] Extension dispatch compatibility confirmed.
- [ ] Documentation updated with final architecture notes if implementation diverges from this epic.

## 10. Useful References

### Internal

- `docs/specs/PRD-pi-csharp-port.md`
- `docs/specs/SDD-pi-csharp-port.md`
- `docs/epics/EPIC-01-core-abstractions.md`
- `docs/epics/EPIC-06-extension-system-and-ts-bridge.md`
- `src/PiSharp.Agent/Harness/AgentHarness.cs`
- `src/PiSharp.Extensions/ExtensionRegistry.cs`
- `src/PiSharp.Extensions/ExtensionEvents.cs`
- `tests/PiSharp.Agent.Tests/Harness/AgentHarnessEventTests.cs`

### External

- SOLID Principles overview: https://en.wikipedia.org/wiki/SOLID
- Mediator pattern reference: https://refactoring.guru/design-patterns/mediator
- Strategy pattern reference: https://refactoring.guru/design-patterns/strategy
- Fowler on Event-Driven style tradeoffs: https://martinfowler.com/articles/201701-event-driven.html
- .NET cancellation guidance: https://learn.microsoft.com/dotnet/standard/threading/cancellation-in-managed-threads
