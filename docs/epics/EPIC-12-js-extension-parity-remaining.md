---
epic_id: EPIC-12
title: JavaScript Extension Parity Remaining Surfaces
status: implemented
priority: high
owner: unassigned
created: 2026-06-01
updated: 2026-06-01
target_version: backlog
related_docs:
  - ../pisharp-typescript-extensions.md
  - ../pisharp-runtime.md
  - ../pisharp-tools.md
  - ../plans/2026-06-01-epic-12-js-extension-parity-design.md
  - ../plans/2026-06-01-epic-12-js-extension-parity-implementation.md
  - ../analysis/ANALYSIS-epic-12-js-extension-parity.md
  - ./EPIC-06-extension-system-and-ts-bridge.md
related_code:
  - ../../src/PiSharp.Runtime/Runtime/PiRuntimeBootstrap.cs
  - ../../src/PiSharp.Runtime/Runtime/SessionRuntime.cs
  - ../../src/PiSharp.Cli/Modes/InteractiveMode.cs
  - ../../src/PiSharp.Cli/Modes/RpcMode.cs
  - ../../src/PiSharp.TsBridge/TsExtensionHost.cs
  - ../../src/PiSharp.TsBridge/Node/TsBridgeRunner.mjs
  - ../../src/PiSharp.Extensions/ExtensionEvents.cs
  - ../../tests/PiSharp.Runtime.Tests/Runtime/SessionRuntimeTests.cs
  - ../../tests/PiSharp.TsBridge.Tests/TsBridgeParityTests.cs
decision_summary: EPIC-12 JavaScript extension parity surfaces have been implemented: loaded-resource runtime actions, resources_discover, user_bash, package-management CLI parity, TS message renderers/decorators bridge, and cross-extension event emit, with security boundaries and deterministic behavior. Object-form package list filters remain deferred.
tags:
  - extensions
  - parity
  - typescript-bridge
  - runtime
  - cli
  - architecture
---

# EPIC-12: JavaScript Extension Parity Remaining Surfaces

## 1. Background And Context

The safe hook baseline defines what was already delivered and the current implementation plan defines what remains:

1. Safe hook baseline delivered input/session lifecycle hooks and cancellation semantics.
2. Loaded-resource runtime actions for TypeScript `pi.resources.list/read` remain part of this epic's implementation plan.

The parity work implemented for this epic covered these surfaces:

1. Loaded-resource TypeScript runtime actions (`pi.resources.list/read`).
2. Dynamic resources discovery hook (resources_discover).
3. User bash hook/surface parity (user_bash).
4. Package-management CLI parity (install/remove/update/list style flows).
5. TypeScript message renderers/decorators bridge parity.
6. Cross-extension event bus emission parity (pi.events.emit style dispatch).

## 2. Problem Statement

PiSharp had extension parity in many core areas, but extensions could not rely on the full JavaScript runtime contract for dynamic resource discovery, user bash interception, package lifecycle operations, rich renderer/decorator registration, and extension-to-extension event signaling.

This limited compatibility for existing JavaScript-first extension ecosystems and forced extension authors to fork behavior by runtime.

Implementation status: these surfaces are now implemented and documented, except for the explicitly deferred object-form package list filters noted in the package-management residual gap.

## 3. Goals And Non-Goals

### 3.1 Goals

1. Implement resources_discover with deterministic bootstrap ordering and clear runtime ownership.
2. Implement user_bash surface and hook semantics consistently across interactive and RPC flows.
3. Add package-management CLI parity commands with extension-aware behavior and predictable output contracts.
4. Bridge TS message renderers/decorators so extension-provided presentation behavior is available in PiSharp clients.
5. Implement cross-extension event emission and delivery with explicit safety, ordering, and error isolation.
6. Preserve architecture boundaries between PiSharp.Extensions, PiSharp.Runtime, PiSharp.Cli, and PiSharp.TsBridge.
7. Add targeted parity tests and docs so behavior is stable and discoverable.

### 3.2 Non-Goals

1. Full redesign of startup architecture beyond what is needed for resources_discover ordering.
2. Arbitrary filesystem or shell escalation beyond existing execution policies.
3. Feature additions unrelated to JavaScript extension parity.
4. Changes to reference-only javascript directory.

## 4. Constraints And Assumptions

1. Resource loading currently occurs before extension host startup; resources_discover requires controlled reordering or staged discovery.
2. user_bash must not bypass existing safety or policy controls in tool/runtime execution.
3. Package commands need parity in behavior but should integrate with existing PiSharp command/runtime abstractions.
4. Renderer/decorator hooks must support non-UI modes by no-op or fallback behavior.
5. Cross-extension events must isolate failures so one extension cannot break others.
6. Existing extension ownership and conflict policies in ExtensionRegistry remain authoritative.

## 5. Scope Breakdown

## 5.1 Surface A: resources_discover

Deliverables:

1. Introduce extension-discoverable resource contribution stage.
2. Define merged resource graph contract and dedupe precedence.
3. Ensure resources_discover can safely enrich resources before runtime consumers finalize context/system prompts.
4. Add tests for ordering, merge precedence, and deterministic output.

Key risks:

1. Bootstrap regressions and startup latency drift.
2. Duplicate or conflicting resource identities.

## 5.2 Surface B: user_bash

Deliverables:

1. Add explicit user bash request surface to interactive/RPC flows.
2. Dispatch user_bash hook with JS-compatible payload/result shape.
3. Implement cancellation/transform/handled semantics aligned with existing input hook patterns.
4. Add tests for cancellation, transformed execution, and audited failure paths.

Key risks:

1. Security boundary erosion if command execution path is not policy-gated.
2. Divergent behavior between interactive and RPC modes.

## 5.3 Surface C: package-management CLI parity

Deliverables:

1. Add package command set in CLI mode with parity-oriented contracts.
2. Integrate package operations into runtime/resource/extension refresh lifecycle.
3. Provide stable JSON outputs for automation scenarios.
4. Add tests for success, invalid package references, and partial-failure handling.

Key risks:

1. State drift between package install results and extension registry state.
2. User confusion if commands succeed but require restart without clear messaging.

Residual gaps:

1. **Object-form package filters not implemented.** The JavaScript CLI supports filtering package listings with object-shaped parameters (e.g., `pi.packages.list({ layer: 'user' })`). PiSharp's `IPackageCommandRunner.ListAsync()` returns all entries without filter support. Implementing object-form filters would require a new request DTO and filter logic in `PiPackageSettingsService.ListAsync()`. This is deferred as a follow-up.

## 5.4 Surface D: TS message renderers/decorators bridge

Deliverables:

1. Add bridge protocols for renderer/decorator registration from TS.
2. Register renderer/decorator metadata in extension runtime contracts.
3. Wire rendering hooks in TUI/CLI-compatible pathways with no-UI fallbacks.
4. Add tests proving registration, precedence, and fallback behavior.

Key risks:

1. Rendering regressions in TUI transcript behavior.
2. Ambiguous ordering when multiple extensions target the same message patterns.

## 5.5 Surface E: cross-extension event emit

Deliverables:

1. Add extension event bus emit API in TS bridge and C# runtime.
2. Define event namespace and payload contract validation.
3. Route events to subscribed extensions with deterministic ordering.
4. Add guardrails for recursion, flood control, and exception isolation.

Key risks:

1. Event storms and reentrancy loops.
2. Hidden coupling between extensions.

## 6. Target State

At epic completion, PiSharp should satisfy:

1. TypeScript `pi.resources.list/read` can access the loaded resource set without exposing arbitrary filesystem reads.
2. resources_discover contributions are available and deterministic before runtime composition points that depend on resources.
3. user_bash is available with secure, policy-constrained behavior across interactive and RPC surfaces.
4. Package-management commands are available with parity-compatible outputs and lifecycle integration.
5. TS renderer/decorator registrations are bridged and applied where supported.
6. Extensions can emit and subscribe to cross-extension events safely.
7. All new behavior has targeted automated tests and documentation.

## 7. PR-Sized Task Plan

### 7.0 PR-0: loaded-resource runtime actions

Scope:

1. Add TypeScript bridge tests for `pi.resources.list/read`.
2. Implement runtime actions for loaded resource metadata and safe content reads.
3. Restrict reads to paths already present in the loaded resource set.

Definition of done:

1. TypeScript extensions can list and read loaded resources.
2. Unknown paths return a friendly failure instead of reading the filesystem.

### 7.1 PR-1: resources_discover bootstrap staging

Scope:

1. Introduce staged resource pipeline (base load, extension discover, finalize).
2. Add resources_discover dispatch in runtime startup.
3. Add resource merge policy and deterministic ordering tests.

Definition of done:

1. resources_discover handlers can contribute resource descriptors.
2. Runtime startup remains deterministic and tested.

### 7.2 PR-2: user_bash runtime and hook surface

Scope:

1. Add user bash command/input surface in interactive and RPC modes.
2. Add JS-compatible user_bash hook dispatch and result handling.
3. Enforce policy checks and diagnostics.

Definition of done:

1. user_bash works in both interactive and RPC pathways.
2. Hook transformations/cancellations are tested.

### 7.3 PR-3: package-management CLI parity commands

Scope:

1. Add package lifecycle commands and runtime integration.
2. Add machine-readable output mode.
3. Add tests for happy path and failure path handling.

Definition of done:

1. Package commands are documented and tested.
2. Extension/resource state refresh behavior is explicit.

### 7.4 PR-4: TS renderers/decorators bridge

Scope:

1. Add bridge protocol and runtime contracts for renderers/decorators.
2. Wire registration and precedence resolution.
3. Add TUI/CLI fallback behavior tests.

Definition of done:

1. TS extension renderer/decorator registration is recognized and applied.
2. Non-UI mode behavior is stable and documented.

### 7.5 PR-5: cross-extension events emit/subscribe

Scope:

1. Add emit API in bridge and runtime event bus.
2. Add validation, recursion guard, and delivery ordering.
3. Add parity tests with multiple extensions.

Definition of done:

1. Extensions can emit and receive cross-extension events safely.
2. Delivery semantics are documented and test-covered.

## 8. Verification Strategy

1. Targeted runtime tests for bootstrap staging, user_bash semantics, and event bus guards.
2. TS bridge parity tests for resources_discover, renderers/decorators, and cross-extension emit.
3. CLI tests for package command behavior and JSON output contracts.
4. Regression tests to ensure existing safe hook behavior remains intact and newly implemented loaded-resource behavior is covered.
5. Full solution build and relevant test projects green before epic closure.

## 9. Closure Criteria

This epic is complete when:

1. All deferred parity surfaces are implemented and documented.
2. Targeted parity tests pass in runtime, CLI, and TS bridge projects.
3. No regressions are introduced in previously delivered parity surfaces.
4. Remaining known gaps (if any) are explicitly tracked in a follow-up epic.

## 10. Implementation Plan

EPIC-12 was implemented from the detailed [JavaScript extension parity implementation plan](../plans/2026-06-01-epic-12-js-extension-parity-implementation.md), with architecture captured in the [design plan](../plans/2026-06-01-epic-12-js-extension-parity-design.md). The JavaScript behavior audit is tracked in [ANALYSIS-epic-12-js-extension-parity.md](../analysis/ANALYSIS-epic-12-js-extension-parity.md).

## 11. Completion Notes

Implemented surfaces:

1. `pi.resources.list/read` exposes loaded resource metadata and safe exact-match content reads.
2. `resources_discover` lets native and TypeScript extensions contribute skill, prompt-template, and theme paths during startup before final composition.
3. `user_bash` is available from interactive `!`/`!!` and RPC `bash`, with first-result-wins hook handling and policy-preserving execution boundaries.
4. Package commands cover install, remove/uninstall, update, and list flows for npm, Git, and local package sources.
5. TypeScript message renderers/decorators register through the bridge and flow into TUI chat row rendering with built-in fallback and safety passes.
6. Cross-extension event emission supports native and TypeScript subscribers with ordered delivery, disposal, diagnostics, and failure isolation.

Deferred follow-up:

1. Object-form package list filters remain unsupported and are tracked in the Surface C residual gap.
