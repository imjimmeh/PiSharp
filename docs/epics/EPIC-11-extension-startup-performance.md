---
epic_id: EPIC-11
title: Extension Startup Performance And Non-Blocking Load Architecture
status: completed
priority: high
owner: unassigned
created: 2026-05-28
updated: 2026-05-28
target_version: backlog
related_docs:
  - ../specs/PRD-pi-csharp-port.md
  - ../specs/SDD-pi-csharp-port.md
  - ./EPIC-06-extension-system-and-ts-bridge.md
  - ../plans/2026-05-26-skills-extensions-loading.md
related_code:
  - ../../src/PiSharp.Runtime/Runtime/PiRuntimeBootstrap.cs
  - ../../src/PiSharp.Runtime/Runtime/SessionRuntime.cs
  - ../../src/PiSharp.Runtime/Runtime/StartupBenchmarkReport.cs
  - ../../src/PiSharp.Cli/Program.cs
  - ../../src/PiSharp.Extensions/ExtensionManager.cs
  - ../../src/PiSharp.Extensions/ExtensionRegistry.cs
  - ../../src/PiSharp.Extensions/ExtensionRuntimeBinding.cs
  - ../../src/PiSharp.TsBridge/TsExtensionHost.cs
  - ../../src/PiSharp.TsBridge/JsonRpc/JsonRpcConnection.cs
  - ../../src/PiSharp.TsBridge/Node/TsBridgeRunner.mjs
  - ../../src/PiSharp.TsBridge/TsBridgeOptions.cs
  - ../../src/PiSharp.Compatibility/Resources/PiResourceLoader.cs
  - ../../tests/PiSharp.Runtime.Tests/Runtime/PiRuntimeBootstrapTests.cs
  - ../../tests/PiSharp.TsBridge.Tests/TsBridgeParityTests.cs
decision_summary: Reduce extension-driven startup latency by instrumenting the TypeScript bridge, adding persistent transpile caching, introducing deterministic batch loading, and then layering descriptor-cache-backed lazy/background extension activation without breaking flags, providers, prompts, tools, or registration ordering.
tags:
  - extensions
  - startup
  - performance
  - typescript-bridge
  - architecture
  - caching
  - reliability
---

# EPIC-11: Extension Startup Performance And Non-Blocking Load Architecture

## 1. Background And Context

### 1.1 Current Startup Profile

A representative startup benchmark shows extension loading dominates PiSharp startup time:

1. Total startup time is about 10.5 seconds.
2. `extensions.total` accounts for about 9.9 seconds.
3. The slowest TypeScript extensions each spend hundreds to thousands of milliseconds in load time.
4. The TypeScript bridge process itself starts quickly relative to extension module load and activation.
5. Native extension discovery is not a meaningful bottleneck in the observed profile.

The observed behavior matches the JavaScript implementation in broad shape: extension modules are discovered and loaded serially before the interactive or print runtime becomes usable. This is not a PiSharp-only regression, but PiSharp has enough control over the C# runtime and TypeScript bridge to improve startup without requiring immediate upstream JavaScript architecture changes.

### 1.2 Current PiSharp Extension Loading Flow

Current flow in `PiRuntimeBootstrap.CreateRuntimeAsync`:

1. Load settings, providers, resources, prompt templates, theme, session, and built-in tools.
2. Create one `ExtensionRegistry` and one `ExtensionManager`.
3. Discover native extension DLLs through `NativePluginHost`.
4. Load and initialize native extensions serially.
5. Create one `TsExtensionHost` for all TypeScript extensions.
6. Start one Node process running `Node/TsBridgeRunner.mjs`.
7. Loop over every TypeScript extension path and call `await tsHost.LoadAsync(path, extensionBinding, token)`.
8. Apply extension flag values after extension registration is complete.
9. Resolve model selection and registered tool lists.
10. Compose prompts, load skills, create `SessionRuntime`, bind extension runtime callbacks, dispatch `session_start`, and then enter the selected CLI mode.

Current flow in `TsBridgeRunner.mjs`:

1. Receive `initialize` or `load_extension` over JSON-RPC.
2. Resolve the extension module path.
3. If the entry is `.ts`, transpile TypeScript to a temporary `.pisharp.mjs` file.
4. Recursively transpile local/package TypeScript imports that can be resolved by the bridge.
5. Import/evaluate the generated module.
6. Run the extension's default export or `activate` function.
7. Send registration RPCs back to C# for tools, commands, shortcuts, flags, providers, prompt sections, and prompt transforms.
8. Flush pending registration RPCs before reporting load success.
9. Delete temporary transpiled files on process exit.

### 1.3 Why This Matters

Extension startup latency currently harms core UX in four ways:

1. Interactive startup blocks before first paint, making PiSharp feel slow even when the user does not immediately use extension functionality.
2. Print and JSON modes pay for extension activation before any prompt work can start.
3. Every startup repeats TypeScript transpilation work because generated modules are temporary.
4. One slow extension delays all later extensions and the whole runtime.

The problem will grow as more extensions are installed globally because default discovery includes global agent extension locations and project-local extension locations.

### 1.4 Triggering Benchmark

The triggering benchmark was run with `--benchmark-startup` and showed:

1. `extensions.total`: about 9.9 seconds.
2. `skills.load`: about 169 ms.
3. `providers.register`: about 160 ms.
4. `resources.load`: about 81 ms.
5. `extensions.ts.bridge.start`: about 66 ms.
6. Multiple TypeScript extensions taking 400 ms to 2.8 seconds each.

The extension phase is therefore the correct target for the next startup-performance epic.

## 2. Problem Statement

Improve extension startup performance while preserving extension semantics.

PiSharp must reduce time-to-usable-runtime by addressing TypeScript extension loading, transpilation, and activation without breaking:

1. Extension flag discovery and CLI validation.
2. Extension provider registration and model resolution.
3. Extension tool registration and active tool selection.
4. Extension prompt contributors, prompt sections, and prompt transforms.
5. Extension command and shortcut registration.
6. Extension event handlers, especially `session_start`.
7. Deterministic registration order and override/conflict behavior.
8. Reload semantics.
9. Benchmark visibility and diagnostics.

## 3. Goals And Non-Goals

### 3.1 Goals

1. Add detailed benchmark attribution inside TypeScript bridge extension loading.
2. Persist TypeScript transpilation artifacts across process starts using a safe content-addressed cache.
3. Add deterministic batch loading for TypeScript extensions.
4. Introduce bounded concurrency only where it preserves current registration semantics.
5. Preserve original extension path order for activation-visible side effects unless a future task explicitly changes semantics.
6. Create a descriptor cache for extension registration metadata where safe.
7. Allow non-critical extensions to load in the background after core runtime readiness where cached descriptors make that safe.
8. Add a coordinator that exposes extension load state, diagnostics, and readiness tasks.
9. Keep startup benchmark output useful by reporting both core readiness and extension readiness.
10. Add regression tests for cache invalidation, deterministic ordering, lazy/proxy behavior, and extension reload.

### 3.2 Non-Goals

1. Removing TypeScript extension compatibility.
2. Requiring extension authors to rewrite existing extensions.
3. Changing default extension discovery rules unless needed for correctness.
4. Replacing JSON-RPC transport with gRPC, named pipes, or another protocol in this epic.
5. Making every extension fully parallel without semantic review.
6. Building a full plugin marketplace or extension trust/sandbox model.
7. Redesigning the JavaScript implementation's extension loader.
8. Making user-configurable per-extension startup policies in the first implementation slice.
9. Optimizing skills, prompt templates, providers, sessions, or themes beyond measurement interactions.

## 4. Constraints And Assumptions

### 4.1 Technical Constraints

1. Current `TsExtensionHost` owns one Node bridge process and routes all TypeScript tool/command/provider callbacks through that process.
2. Current `JsonRpcConnection.RequestAsync` supports concurrent outbound requests, but the bridge runner currently handles incoming requests one line at a time.
3. `ExtensionRegistry` is thread-safe for registry mutations, but deterministic registration order still matters for conflicts and override policy.
4. `ExtensionRuntimeBinding` may be called during extension activation, including runtime queries such as `getAllTools`, `getActiveTools`, and flag access.
5. Extension flags must be known before CLI unknown flag values can be validated.
6. Extension providers may affect model resolution.
7. Prompt sections/transforms must be known before final system prompt composition unless cached descriptors are used.
8. Tools must be registered before active tool names and prompt tool snippets are finalized unless proxy registrations are used.
9. `session_start` handlers can register tools after runtime binding and event forwarding are active.
10. The current TypeScript transpile path writes temporary files beside source files, which is not appropriate for a persistent cache and is unsafe to parallelize naively.

### 4.2 Product Constraints

1. Startup should become visibly faster for users with multiple global extensions.
2. Extension diagnostics must remain understandable; silent background failures are unacceptable.
3. Users must not lose extension tools, flags, commands, prompts, or providers after optimization.
4. `--help` must remain accurate when extension flags are involved.
5. `--benchmark-startup` must explain what moved to cache or background rather than hiding costs.
6. Reload must remain predictable and should be able to bypass stale cached descriptors.

### 4.3 Design Assumptions

1. Persistent transpile caching is the safest first performance win because it preserves eager activation semantics.
2. Batch loading should be introduced before background loading so deterministic ordering and bridge protocol shape are explicit.
3. Full concurrent activation is not safe as a first step because extension activation can observe runtime state and produce conflicting registrations.
4. Lazy/background loading requires cached registration descriptors or manifest-like metadata to avoid missing flags, providers, tools, and prompts at runtime composition time.
5. A coordinator object should own extension readiness state rather than spreading load tasks through `PiRuntimeBootstrap`, `SessionRuntime`, and `TsExtensionHost`.
6. Multi-process bridge pooling should be deferred until measurement proves single-process import/evaluation remains a bottleneck after caching and batch improvements.

## 5. Current Gap Matrix

### 5.1 Measurement Gap

Current startup benchmarks report per-extension load duration but do not break down time spent in transpilation, module import/evaluation, activation, registration flushing, runtime action waits, cache hits, or bridge-side errors.

### 5.2 Transpile Cache Gap

The TypeScript bridge writes generated modules to temporary files and deletes them on exit. Every startup repeats TypeScript compiler loading, source reads, dependency transpilation, import rewriting, and generated file writes.

### 5.3 Serial Load Gap

`PiRuntimeBootstrap` calls `LoadAsync` once per extension in a serial loop. The runner also processes `initialize` extension paths serially. One expensive extension blocks every later extension and blocks runtime creation.

### 5.4 Ordered Concurrency Gap

There is no protocol that distinguishes parallel-safe preparation from order-sensitive activation/registration. Naive `Task.WhenAll` around `LoadAsync` would risk nondeterministic registration behavior and shared transpile races.

### 5.5 Descriptor Cache Gap

PiSharp has no persisted summary of extension registrations. Startup must evaluate each extension before knowing flags, tools, commands, providers, prompts, or shortcuts.

### 5.6 Background Readiness Gap

`SessionRuntime` has no first-class model for extensions that are pending, loading, ready, failed, stale, or lazily activated. Diagnostics are emitted during startup, but there is no ongoing readiness state for the TUI or commands to inspect.

### 5.7 Reload Gap

`SessionRuntime.ReloadExtensionsAsync` unloads registrations and re-runs the same eager load path. It does not differentiate cache refresh, descriptor replay, background activation, or extension load failures.

### 5.8 Benchmark Semantics Gap

If extension loading becomes cached or backgrounded, the current `Total` and `extensions.total` output will be insufficient. Users need to distinguish core runtime readiness from full extension readiness.

## 6. Target State

At epic completion, PiSharp should satisfy:

1. Startup benchmark output attributes TypeScript extension time to bridge startup, cache lookup, transpile, import, activation, registration flush, and runtime callback waits.
2. TypeScript transpilation outputs are persisted in a content-addressed cache outside extension source directories.
3. Cache invalidation is deterministic and based on source content, dependencies, compiler options, bridge cache version, and TypeScript compiler version when available.
4. TypeScript extension loading has a batch API that can prepare work concurrently while preserving deterministic activation-visible semantics.
5. The runtime never uses naive unordered concurrent activation for extensions that can observe or mutate shared registration state.
6. Descriptor cache replay can register safe metadata before full activation when the cache is valid.
7. Proxy tool/command/provider registrations can await actual extension readiness if invoked before background activation finishes.
8. A runtime extension load coordinator exposes extension readiness state and diagnostics.
9. Interactive mode can become usable before all non-critical extension modules finish loading when cached descriptors are available.
10. `--help`, extension flag validation, model resolution, prompt composition, active tools, and `session_start` behavior remain correct.
11. Extension reload can refresh descriptors and clear stale cache state.
12. Tests prove deterministic ordering, cache invalidation, descriptor replay, proxy invocation, and reload behavior.

## 7. PR-Sized Task Breakdown

### 7.1 Task 1: Add Bridge-Side Startup Timing And Diagnostics

Scope:

1. Extend `TsBridgeRunner.mjs` to time load sub-phases for each extension.
2. Capture at least: cache lookup, TypeScript compiler load, transpile, dependency transpile, module import, activation, registration flush, and total bridge-side duration.
3. Return detailed timing data from `load_extension` and the new batch load method once available.
4. Extend `TsExtensionHost` to deserialize timing data into typed C# records.
5. Extend `StartupBenchmarkReport` and formatter to show detailed TypeScript extension sub-phases without overwhelming normal output.
6. Include success/failure and error message attribution per extension.

Primary files:

- ../../src/PiSharp.TsBridge/Node/TsBridgeRunner.mjs
- ../../src/PiSharp.TsBridge/TsExtensionHost.cs
- ../../src/PiSharp.TsBridge/Protocol/TsBridgeContracts.cs
- ../../src/PiSharp.Runtime/Runtime/StartupBenchmarkReport.cs
- ../../src/PiSharp.Cli/Program.cs
- ../../tests/PiSharp.TsBridge.Tests/TsBridgeParityTests.cs
- ../../tests/PiSharp.Runtime.Tests/Runtime/PiRuntimeBootstrapTests.cs

Definition of Done:

1. `--benchmark-startup` reports per-extension sub-phase details.
2. Existing benchmark tests still pass with new report shape.
3. Bridge timing data is available to future tasks without parsing strings.
4. Failed extension loads include timing up to failure.

Acceptance Criteria:

1. A slow extension can be identified as slow due to transpile, import, activation, or registration flush.
2. Bridge startup time remains separately reported.
3. Benchmark output remains stable and deterministic enough for tests.

Tests:

1. Unit or integration test for timing fields returned by the bridge.
2. Formatter test for detailed TypeScript extension timings.
3. Existing benchmark capture tests updated for new fields.

### 7.2 Task 2: Introduce Persistent TypeScript Transpile Cache

Scope:

1. Add cache directory resolution to `TsBridgeOptions` or bridge initialization parameters.
2. Store transpiled `.mjs` outputs in a PiSharp-managed cache directory, not beside source files.
3. Use content-addressed cache keys including source content hash, bridge cache schema version, compiler options, and TypeScript version when available.
4. Cache dependency transpilation outputs and rewrite imports to cached dependency paths.
5. Use atomic writes to avoid corrupted cache entries under concurrent or interrupted runs.
6. Add cache hit/miss fields to bridge load timing data.
7. Add a conservative fallback path if cache read/write fails.
8. Add cleanup policy or leave room for a follow-up cleanup task if cache growth is controlled by content hashing and versioning.

Primary files:

- ../../src/PiSharp.TsBridge/Node/TsBridgeRunner.mjs
- ../../src/PiSharp.TsBridge/TsBridgeOptions.cs
- ../../src/PiSharp.Runtime/Runtime/PiRuntimeBootstrap.cs
- ../../tests/PiSharp.TsBridge.Tests/TsBridgeParityTests.cs

Definition of Done:

1. A `.ts` extension is transpiled once and reused on a second process start when unchanged.
2. Changing extension source invalidates the cache entry.
3. Changing a local TypeScript dependency invalidates or regenerates the correct cached dependency path.
4. Cache failures degrade to uncached transpilation with diagnostics rather than startup failure.

Acceptance Criteria:

1. Second startup of unchanged TypeScript extensions reports cache hits.
2. Generated cache files do not appear beside extension source files.
3. Extension imports still resolve for local relative `.ts` dependencies.
4. Existing `.mjs` extension behavior is unchanged.

Tests:

1. Cache hit test across two bridge processes.
2. Cache invalidation test after source modification.
3. Dependency invalidation test for relative `.ts` import.
4. Fallback test when cache directory is unavailable if practical.

### 7.3 Task 3: Add Deterministic Batch Loading Protocol

Scope:

1. Add `TsExtensionHost.LoadManyAsync(...)` for TypeScript extension batches.
2. Add a bridge method such as `load_extensions` that accepts extension paths and an optional concurrency setting.
3. Split bridge work into preparation/import-safe work and activation/registration-sensitive work.
4. Permit bounded concurrent preparation where safe.
5. Preserve original path order for activation and registration flushing.
6. Return one result per extension with timing, success, and error data.
7. Update `PiRuntimeBootstrap` to use batch loading instead of a serial C# loop.
8. Keep `LoadAsync` for single-extension reload and tests, implemented through the same internal path where practical.

Primary files:

- ../../src/PiSharp.TsBridge/TsExtensionHost.cs
- ../../src/PiSharp.TsBridge/Node/TsBridgeRunner.mjs
- ../../src/PiSharp.TsBridge/Protocol/TsBridgeContracts.cs
- ../../src/PiSharp.Runtime/Runtime/PiRuntimeBootstrap.cs
- ../../tests/PiSharp.TsBridge.Tests/TsBridgeParityTests.cs
- ../../tests/PiSharp.Runtime.Tests/Runtime/PiRuntimeBootstrapTests.cs

Definition of Done:

1. Runtime startup uses batch loading for TypeScript extensions.
2. Batch loading preserves deterministic registration order.
3. Batch result captures all extension outcomes.
4. Existing extension parity tests continue to pass.

Acceptance Criteria:

1. Multiple extensions that register the same key behave in the same winner order as before.
2. A failed extension is attributed to its path and does not corrupt other result records.
3. Startup benchmark still shows each extension separately.

Tests:

1. Batch load success test for multiple extensions.
2. Deterministic conflict/override ordering test.
3. Partial failure diagnostics test.
4. Existing single-extension tests remain green.

### 7.4 Task 4: Harden Registration Ordering And Runtime Action Semantics

Scope:

1. Audit registration calls for order-sensitive behavior: tools, providers, prompt sections, flags, commands, shortcuts, and transforms.
2. Ensure batch loading cannot allow registration RPCs from later extensions to overtake earlier extensions during ordered activation.
3. Ensure activation-time runtime actions such as `getAllTools`, `getActiveTools`, and `setActiveTools` observe the same state they observe today unless intentionally changed.
4. Add tests for extension A observing registrations before/after extension B according to established serial semantics.
5. Document which phases are safe to parallelize and which phases must remain ordered.

Primary files:

- ../../src/PiSharp.TsBridge/Node/TsBridgeRunner.mjs
- ../../src/PiSharp.Extensions/ExtensionRegistry.cs
- ../../src/PiSharp.Runtime/Runtime/PiRuntimeBootstrap.cs
- ../../tests/PiSharp.TsBridge.Tests/TsBridgeParityTests.cs
- ../../tests/PiSharp.Runtime.Tests/Runtime/PiRuntimeBootstrapTests.cs

Definition of Done:

1. Ordered activation semantics are test-proven.
2. Registration RPC flushing is deterministic.
3. Runtime action observations during activation are intentional and documented.
4. The code clearly prevents naive unordered parallel activation from being introduced accidentally.

Acceptance Criteria:

1. No nondeterministic test failures under repeated test runs.
2. Conflicting registrations resolve predictably.
3. Runtime action behavior matches current serial behavior unless a task explicitly changes it with tests.

Tests:

1. Repeated deterministic ordering test.
2. Activation-time `getAllTools` observation test.
3. Duplicate registration conflict test.

### 7.5 Task 5: Introduce Extension Descriptor Cache

Scope:

1. Define a persisted descriptor schema for safe extension registration metadata.
2. Include source path, source hash/dependency hash, bridge descriptor schema version, extension load result metadata, and registration descriptors.
3. Persist descriptors after a successful eager load.
4. Replay valid descriptors on startup before full activation where safe.
5. Include descriptors for tools, commands, shortcuts, flags, prompt sections, prompt transforms, and provider metadata where possible.
6. Mark descriptors that require full activation before use.
7. Add descriptor invalidation when source, dependency, or schema changes.
8. Keep descriptor replay behind a feature flag or internal option until tests prove safety.

Primary files:

- ../../src/PiSharp.TsBridge/Node/TsBridgeRunner.mjs
- ../../src/PiSharp.TsBridge/TsExtensionHost.cs
- ../../src/PiSharp.TsBridge/Protocol/TsBridgeContracts.cs
- ../../src/PiSharp.Extensions/ExtensionRegistry.cs
- ../../src/PiSharp.Runtime/Runtime/PiRuntimeBootstrap.cs
- ../../tests/PiSharp.TsBridge.Tests/TsBridgeParityTests.cs
- ../../tests/PiSharp.Runtime.Tests/Runtime/PiRuntimeBootstrapTests.cs

Definition of Done:

1. Successful extension loads write descriptor cache entries.
2. Valid descriptors can be replayed into the C# registry without evaluating the module.
3. Stale descriptors are ignored and replaced by eager load results.
4. Descriptor replay is observable in benchmark output.

Acceptance Criteria:

1. Extension flags from valid descriptor cache are available for CLI help/validation.
2. Prompt sections from valid descriptor cache participate in prompt composition.
3. Tool descriptors from valid descriptor cache appear in registered tools, with execution routed through activation/proxy logic in later tasks.
4. Invalid descriptors never silently mask changed extension behavior.

Tests:

1. Descriptor write/read round-trip test.
2. Descriptor replay test for flags and prompt sections.
3. Stale descriptor invalidation test.
4. Schema version invalidation test.

### 7.6 Task 6: Add Lazy Proxy Registrations For Descriptor-Replayed Extensions

Scope:

1. Introduce proxy registrations for tools and commands whose descriptors are replayed before full extension activation.
2. When a proxy tool/command/provider callback is invoked, ensure the owning extension is activated or await its in-flight activation.
3. Route invocation to the real bridge handler after readiness.
4. Surface clear errors if activation fails before invocation.
5. Replace proxy registrations with real registrations after activation when appropriate.
6. Keep prompt-only and flag-only descriptor replay safe without requiring immediate activation.

Primary files:

- ../../src/PiSharp.Runtime/Runtime/SessionRuntime.cs
- ../../src/PiSharp.Runtime/Runtime/PiRuntimeBootstrap.cs
- ../../src/PiSharp.TsBridge/TsExtensionHost.cs
- ../../src/PiSharp.TsBridge/TsBridgeTool.cs
- ../../src/PiSharp.Extensions/ExtensionRegistry.cs
- ../../tests/PiSharp.Runtime.Tests/Runtime/PiRuntimeBootstrapTests.cs
- ../../tests/PiSharp.TsBridge.Tests/TsBridgeParityTests.cs

Definition of Done:

1. Descriptor-replayed tools and commands are visible before full activation.
2. Invoking a proxy waits for or triggers actual activation.
3. Activation failure is reported as a tool/command error rather than a crash.
4. Real registrations replace or validate proxies after activation.

Acceptance Criteria:

1. A cached extension tool appears in `Harness.AllToolNames` at startup.
2. Calling that tool activates the extension if needed and invokes the real handler.
3. If activation fails, the user sees an actionable extension diagnostic.
4. Tool registration ownership and unregister-by-source behavior remain correct.

Tests:

1. Proxy tool invocation activates extension.
2. Proxy command invocation activates extension.
3. Activation failure from proxy invocation returns deterministic error.
4. Reload removes proxies and real registrations by source.

### 7.7 Task 7: Add Extension Load Coordinator And Readiness State

Scope:

1. Add an extension load coordinator owned by runtime startup or `SessionRuntime`.
2. Track per-extension states: discovered, descriptor replayed, pending, loading, ready, failed, stale, disabled.
3. Expose `ExtensionsReadyTask` or equivalent for callers that need full readiness.
4. Expose diagnostics for failed background loads.
5. Coordinate lazy proxy activation and background activation so duplicate loads do not occur.
6. Integrate coordinator with reload so old state is invalidated cleanly.
7. Ensure disposal cancels or awaits background work safely.

Primary files:

- ../../src/PiSharp.Runtime/Runtime/SessionRuntime.cs
- ../../src/PiSharp.Runtime/Runtime/PiRuntimeBootstrap.cs
- ../../src/PiSharp.TsBridge/TsExtensionHost.cs
- ../../src/PiSharp.Extensions/ExtensionManager.cs
- ../../tests/PiSharp.Runtime.Tests/Runtime/PiRuntimeBootstrapTests.cs

Definition of Done:

1. Extension readiness is represented by one runtime-level abstraction.
2. Background and lazy activation share the same state machine.
3. Diagnostics can be inspected after startup.
4. Runtime disposal does not leave bridge tasks unmanaged.

Acceptance Criteria:

1. Interactive mode can know whether extensions are still loading.
2. Reload can wait for old loads, cancel them, or replace them deterministically.
3. Proxy invocation and background loading cannot double-activate the same extension.

Tests:

1. Coordinator state transition tests.
2. Concurrent proxy invocation deduplication test.
3. Reload invalidates previous load state test.
4. Dispose during background load test if practical.

### 7.8 Task 8: Enable Background Loading For Non-Critical Extensions

Scope:

1. Define critical vs non-critical extension startup requirements.
2. Block startup for extensions with missing/stale descriptors that are needed for flags, selected providers, prompt composition, or active tool metadata.
3. Allow valid descriptor-replayed extensions to activate in the background after core runtime composition.
4. Ensure `session_start` event behavior is deterministic: either wait for all session-start-capable extensions or document and implement a replay/late-session-start policy.
5. Add user-visible diagnostics for extensions still loading or failed after startup.
6. Add startup benchmark fields for core ready time vs extensions ready time.

Primary files:

- ../../src/PiSharp.Runtime/Runtime/PiRuntimeBootstrap.cs
- ../../src/PiSharp.Runtime/Runtime/SessionRuntime.cs
- ../../src/PiSharp.Runtime/Runtime/StartupBenchmarkReport.cs
- ../../src/PiSharp.Cli/Program.cs
- ../../tests/PiSharp.Runtime.Tests/Runtime/PiRuntimeBootstrapTests.cs

Definition of Done:

1. Startup can complete before all descriptor-replayed extensions are fully activated.
2. Critical startup requirements still block when necessary.
3. Benchmark output distinguishes core readiness and full extension readiness.
4. `session_start` semantics are explicitly implemented and tested.

Acceptance Criteria:

1. With valid descriptors, runtime becomes usable before background activation completes.
2. Extension tools remain visible and invoke correctly through proxies.
3. A background load failure does not make startup appear successful without a diagnostic.
4. `--benchmark-startup` reports both user-perceived ready time and total extension ready time.

Tests:

1. Runtime returns before artificial slow background extension completes.
2. Critical extension with stale descriptor still blocks or fails predictably.
3. `session_start` late/background policy test.
4. Benchmark formatter test for core-ready vs extensions-ready timings.

### 7.9 Task 9: Update Reload Semantics For Cache And Background Loading

Scope:

1. Update `SessionRuntime.ReloadExtensionsAsync` to coordinate descriptor invalidation, proxy removal, background task cancellation, and fresh activation.
2. Add options or internal modes for reload: normal cached reload and forced fresh reload.
3. Ensure `ExtensionManager.Unload` removes descriptor-replayed proxies and real registrations consistently.
4. Ensure providers registered by stale extensions are unregistered and restored in correct order.
5. Refresh benchmark/diagnostic state after reload where appropriate.

Primary files:

- ../../src/PiSharp.Runtime/Runtime/SessionRuntime.cs
- ../../src/PiSharp.Runtime/Runtime/PiRuntimeBootstrap.cs
- ../../src/PiSharp.Extensions/ExtensionManager.cs
- ../../src/PiSharp.Extensions/ExtensionRegistry.cs
- ../../src/PiSharp.TsBridge/TsExtensionHost.cs
- ../../tests/PiSharp.Runtime.Tests/Runtime/PiRuntimeBootstrapTests.cs

Definition of Done:

1. Reload removes old registrations and load state before applying new state.
2. Cached descriptors are refreshed or bypassed according to reload mode.
3. Background loads cannot mutate the registry after their generation has been invalidated.
4. Reload tests cover providers, tools, flags, and prompts where practical.

Acceptance Criteria:

1. `/reload`-style extension reload does not leave duplicate tools or stale providers.
2. Reload after source edit reflects the edited extension.
3. Failed reload leaves clear diagnostics and a consistent registry.

Tests:

1. Reload after source modification test.
2. Reload during background load test.
3. Unload proxy and real registration cleanup test.
4. Provider unregister/restore test if provider descriptors are implemented.

### 7.10 Task 10: Optional Bridge Worker Pool Evaluation

Scope:

1. Use post-cache benchmarks to decide whether import/evaluation remains a large bottleneck.
2. Prototype or design a `TsExtensionHostManager` that owns multiple bridge workers.
3. Assign each extension to an owning worker and route tool/command/provider callbacks to that worker.
4. Broadcast events to workers with deterministic ordering guarantees.
5. Evaluate memory cost, process lifecycle cost, and error isolation benefit.
6. Implement only if benchmark evidence justifies the complexity.

Primary files:

- ../../src/PiSharp.TsBridge/TsExtensionHost.cs
- ../../src/PiSharp.Runtime/Runtime/PiRuntimeBootstrap.cs
- ../../src/PiSharp.Runtime/Runtime/SessionRuntime.cs
- ../../tests/PiSharp.TsBridge.Tests/TsBridgeParityTests.cs
- ../../tests/PiSharp.Runtime.Tests/Runtime/PiRuntimeBootstrapTests.cs

Definition of Done:

1. A written decision exists: implement worker pool now, defer it, or reject it.
2. If implemented, callback routing and event broadcasting are test-proven.
3. If deferred, benchmark data explains why.

Acceptance Criteria:

1. No worker-pool implementation is merged without benchmark evidence.
2. Existing single-host behavior remains available as a fallback.
3. Multi-worker mode does not change extension registration semantics unexpectedly.

Tests:

1. Worker assignment test if implemented.
2. Callback routing test if implemented.
3. Event broadcast test if implemented.
4. Single-worker fallback test.

## 8. Cross-Cutting Definition Of Done (Epic Level)

The epic is complete only when all of the following are true:

1. Extension startup benchmarking attributes bridge-side costs to actionable sub-phases.
2. TypeScript transpile artifacts are cached persistently and invalidated safely.
3. Batch loading replaces the serial C# TypeScript extension loop.
4. Batch loading preserves deterministic activation and registration semantics.
5. Descriptor cache replay can safely populate startup-critical registration metadata.
6. Lazy proxy registrations correctly await or trigger activation when invoked.
7. Background extension loading is coordinated through a runtime-owned state machine.
8. Startup benchmark output distinguishes core ready time from full extension ready time when background loading is enabled.
9. Extension reload handles cache, proxies, background tasks, and stale generation cleanup.
10. Existing extension parity tests pass.
11. New tests cover cache hit/miss, cache invalidation, ordering, descriptor replay, proxy invocation, background readiness, and reload.
12. `dotnet format PiSharp.sln --verify-no-changes --no-restore` passes.
13. `dotnet test PiSharp.sln --no-restore` passes.

## 9. Acceptance Criteria (Epic Level)

1. A user with multiple installed TypeScript extensions sees meaningfully lower repeated startup time after cache warm-up.
2. `--benchmark-startup` explains where extension time is spent and whether cache/background paths were used.
3. Extension flags remain available to `--help` and CLI flag validation.
4. Extension tools, commands, prompts, providers, and shortcuts remain available with correct source ownership.
5. Extensions that register duplicate or overriding items resolve in deterministic path order.
6. Lazy/proxy invocation of a descriptor-replayed tool or command produces the same result as eager activation.
7. A failed background extension activation produces a visible diagnostic and does not corrupt runtime state.
8. Extension reload reflects source changes and removes stale registrations.
9. The implementation avoids naive unordered `Task.WhenAll` activation semantics.
10. Tests prove the optimized paths and the fallback eager path.

## 10. Test Strategy

### 10.1 Unit Tests

1. Cache key construction and schema version invalidation.
2. Descriptor serialization/deserialization.
3. Extension load coordinator state transitions.
4. Proxy registration activation gating.
5. Benchmark report formatting for sub-phases and readiness times.

### 10.2 Bridge Integration Tests

1. `.ts` extension cache hit across two bridge processes.
2. Source edit invalidates cache.
3. Local dependency edit invalidates or regenerates dependency cache.
4. Batch load returns one result per extension.
5. Ordered activation preserves duplicate registration behavior.
6. Failed extension result includes path, error, and timing data.

### 10.3 Runtime Integration Tests

1. Runtime startup uses batch loading and records per-extension timings.
2. Descriptor replay makes extension flags and prompt sections available before activation.
3. Proxy tool invocation activates the owning extension.
4. Background activation allows runtime creation before a slow extension completes when descriptors are valid.
5. Critical stale descriptors block or eager-load as intended.
6. Reload clears stale registrations and refreshes descriptors.

### 10.4 Regression Tests

1. Existing `TsBridgeParityTests` remain green.
2. Existing `PiRuntimeBootstrapTests` remain green.
3. Session-start extension registration still works.
4. Activation-time runtime actions still observe documented tool lists.
5. Provider registration/unregistration remains source-owned.
6. Descriptor/proxy cleanup does not leak tools after unload.

### 10.5 Build And CI Guardrails

1. Run targeted tests for each task slice.
2. For final epic completion, run `dotnet format PiSharp.sln --verify-no-changes --no-restore`.
3. For final epic completion, run `dotnet test PiSharp.sln --no-restore`.
4. Treat benchmark output changes as a compatibility surface for tests and documentation.

## 11. Risks And Mitigations

1. Risk: Naive concurrent extension activation causes nondeterministic registration winners.
   Mitigation: Split preparation/import from activation and preserve original path order for activation-visible side effects.

2. Risk: Persistent cache serves stale generated code.
   Mitigation: Key cache entries by source content, dependency content, compiler options, bridge cache schema, and TypeScript version where available.

3. Risk: Cache writes corrupt outputs under concurrent starts.
   Mitigation: Use content-addressed paths and atomic temp-file-to-final-file moves.

4. Risk: Descriptor replay hides changed extension registrations.
   Mitigation: Invalidate descriptors on source/dependency/schema changes and compare activation results against descriptors where feasible.

5. Risk: Background loading misses `session_start` behavior.
   Mitigation: Either block session start until handlers are known, or implement an explicit late replay policy with tests.

6. Risk: Extension providers are needed before model resolution.
   Mitigation: Treat provider descriptors as critical for selected/default model resolution, and eager-load if descriptor confidence is insufficient.

7. Risk: Proxy tools appear available but fail at first use.
   Mitigation: Surface clear activation diagnostics and return deterministic tool/command errors rather than throwing.

8. Risk: New coordinator increases runtime complexity.
   Mitigation: Keep state transitions explicit, tested, and owned by one runtime abstraction.

9. Risk: Multi-process bridge pool increases memory and event routing complexity.
   Mitigation: Defer worker pool until benchmarks prove cache/batch/background work is insufficient.

10. Risk: Benchmark improvements are hard to compare across cold/warm cache states.
    Mitigation: Report cache hit/miss counts and separate cold eager time from warm core-ready time.

## 12. Dependencies

1. EPIC-06 extension system and TypeScript bridge foundation.
2. Current `TsBridgeRunner.mjs` TypeScript transpilation and import rewriting behavior.
3. Current `ExtensionRegistry` source ownership and override semantics.
4. Current `SessionRuntime` extension runtime binding and reload hooks.
5. Existing benchmark collector and formatter.
6. Existing TypeScript bridge and runtime test suites.

## 13. Out Of Scope / Follow-Ups

1. Extension marketplace, trust, signing, or sandboxing.
2. User-facing per-extension startup policy UI.
3. JavaScript implementation parity changes.
4. Non-extension startup optimization.
5. Replacing JSON-RPC with another transport.
6. Mandatory extension author manifests.
7. Extension dependency graph semantics beyond cache dependency tracking.
8. Cross-project cache sharing policy beyond safe content-addressed storage.

## 14. Implementation Notes For Reviewers

1. Do not approve a change that simply wraps existing `LoadAsync` calls in `Task.WhenAll`; that is not semantically safe.
2. Preserve deterministic path order unless a task explicitly documents and tests a changed ordering contract.
3. Treat extension flags, providers, prompt transforms, prompt sections, and active tool lists as startup-critical surfaces.
4. Keep bridge timing structured, not string-only.
5. Persistent cache files must not be written beside extension source files.
6. Descriptor replay must be conservative; stale or uncertain descriptors should fall back to eager load.
7. Background loading must have visible diagnostics and a clear readiness state.
8. Every optimization must keep the eager no-cache path working.
9. Prefer small PRs that introduce one concept at a time: timing, cache, batch, descriptors, proxies, coordinator, background policy, reload.
10. Benchmark claims should include cold-cache and warm-cache numbers when possible.

## 15. Suggested Milestone Plan

1. Milestone A: bridge timing and benchmark output improvements.
2. Milestone B: persistent transpile cache with invalidation tests.
3. Milestone C: deterministic batch loading with ordered activation.
4. Milestone D: descriptor cache write/replay for startup-critical metadata.
5. Milestone E: lazy proxy registrations and activation gating.
6. Milestone F: extension load coordinator and readiness state.
7. Milestone G: background loading policy and core-ready benchmark reporting.
8. Milestone H: reload hardening and final regression suite.
9. Milestone I: worker pool decision based on post-cache benchmarks.

## 16. Implementation Checklist (Per-PR Template)

Use this checklist in every PR linked to this epic. Copy into the PR description and fill it out.

### 16.1 Scope And Traceability

- [ ] PR title follows: `EPIC-11 / Task X / <area>`
- [ ] PR links to EPIC-11 and specific task number(s) from Section 7
- [ ] PR states whether it is `feature`, `performance`, `refactor-only`, or `test-only`
- [ ] PR includes a short "out of scope" list
- [ ] PR lists the exact files changed
- [ ] PR identifies whether it changes eager, cached, lazy, background, or reload behavior

### 16.2 Semantic Safety

- [ ] Extension registration order remains deterministic
- [ ] Extension flags remain available before CLI flag validation
- [ ] Extension providers remain available before model resolution when needed
- [ ] Extension prompt registrations remain available before prompt composition when needed
- [ ] Extension tools remain available before active tool selection when needed
- [ ] `session_start` behavior is unchanged or explicitly covered by the task
- [ ] Eager no-cache behavior still works

### 16.3 Cache And Descriptor Safety

Mark only items touched by the PR.

- [ ] Cache key includes schema/version information
- [ ] Cache invalidates on source changes
- [ ] Cache invalidates on dependency changes where relevant
- [ ] Cache writes are atomic
- [ ] Cache failures fall back safely
- [ ] Descriptor schema has versioning
- [ ] Stale descriptors are ignored
- [ ] Descriptor replay does not hide activation failures
- [ ] Cache files are not written into extension source directories

### 16.4 Benchmark Coverage Checklist

- [ ] New or changed timing fields are represented in `StartupBenchmarkReport`
- [ ] Benchmark formatter output is updated and tested
- [ ] Cold-cache and warm-cache behavior are distinguishable where relevant
- [ ] Core-ready and extension-ready timings are distinguishable if background loading changed
- [ ] Failure timings include path and error details

### 16.5 Test Coverage Checklist

- [ ] Added or updated TypeScript bridge tests for changed bridge behavior
- [ ] Added or updated runtime bootstrap tests for changed startup behavior
- [ ] Added or updated reload tests if unload/reload behavior changed
- [ ] Added or updated ordering tests if concurrency changed
- [ ] Added or updated cache invalidation tests if cache behavior changed
- [ ] Existing parity tests remain green

### 16.6 Manual Verification Checklist

- [ ] Ran `dotnet run --project src/PiSharp.Cli/PiSharp.Cli.csproj -- --benchmark-startup` or equivalent from the correct project path
- [ ] Compared cold-cache and warm-cache startup where applicable
- [ ] Verified at least one installed TypeScript extension still registers tools/commands
- [ ] Verified `--help` includes extension flags when present
- [ ] Verified reload reflects a changed extension source file when relevant
- [ ] Verified diagnostics are visible for failed extension load when relevant

### 16.7 Quality Gates

- [ ] No unrelated startup refactors mixed into PR
- [ ] No broad extension behavior changes without tests
- [ ] No fire-and-forget task without ownership, cancellation, and diagnostics
- [ ] No unbounded concurrency
- [ ] No source-directory cache artifacts
- [ ] Naming aligns with existing PiSharp conventions
- [ ] `dotnet format PiSharp.sln --verify-no-changes --no-restore` passes for final epic PR
- [ ] Relevant tests pass; final epic PR runs `dotnet test PiSharp.sln --no-restore`

### 16.8 Definition Of Done Confirmation (Per PR)

- [ ] Task-specific DoD items from Section 7 are complete
- [ ] Task-specific acceptance criteria from Section 7 are satisfied
- [ ] Test expectations from Section 7 are satisfied
- [ ] Any remaining follow-up work is captured as a concrete TODO issue/task

### 16.9 PR Summary Template

Use this exact structure in PR descriptions:

```md
## EPIC-11 Task Mapping

- Task: 7.X
- Type: feature | performance | refactor-only | test-only

## What Changed

- ...

## Startup/Extension Semantics

- Eager path: ...
- Cached path: ...
- Lazy/proxy path: ...
- Background path: ...
- Reload path: ...

## Ordering And Safety

- Registration order impact: ...
- Critical startup surfaces: ...
- Fallback behavior: ...

## Benchmarks

- Cold cache: ...
- Warm cache: ...
- Core ready: ...
- Extensions ready: ...

## Tests

- Unit: ...
- Bridge integration: ...
- Runtime integration: ...
- Manual: ...

## Risks

- ...

## Out Of Scope

- ...
```

### 16.10 Final Epic Exit Checklist

Use at epic close-out in addition to Section 8:

- [ ] All Section 7 tasks are complete or explicitly deferred
- [ ] Deferred items have owner + follow-up issue
- [ ] Benchmark report includes before/after cold-cache data
- [ ] Benchmark report includes before/after warm-cache data
- [ ] Extension cache architecture is documented
- [ ] Descriptor replay/proxy behavior is documented
- [ ] Background readiness behavior is documented
- [ ] Reload behavior is documented
- [ ] Worker pool decision is documented
- [ ] Epic status is updated from `backlog` or `in_progress` to final state

## 17. Implementation Summary

### 17.1 Completed Before This Epic

1. EPIC-06 introduced the extension system and TypeScript bridge foundation.
2. `TsBridgeRunner.mjs` can load `.mjs` and `.ts` extension entries.
3. The bridge can register tools, providers, commands, shortcuts, flags, prompt sections, and prompt transforms.
4. `PiRuntimeBootstrap` records broad startup phases and per-extension load totals.
5. Runtime tests prove TypeScript extensions can register tools during `session_start`.
6. The observed benchmark identified extension loading as the dominant startup cost.

### 17.2 Current Decisions

1. Persistent transpile caching is the first optimization target.
2. Batch loading must preserve deterministic activation and registration semantics.
3. Descriptor replay is required before safe non-blocking/background extension activation.
4. Proxy registrations are required before descriptor-replayed tools/commands can be considered user-safe.
5. Background activation should be coordinated centrally and made visible through diagnostics/readiness state.
6. Multi-process bridge pooling is a later decision, not the first implementation move.

### 17.3 Completed In This Implementation

No implementation has been completed yet. This epic defines the work plan for extension startup performance.

### 17.4 Remaining Follow-Ups

1. User-facing extension startup policy controls may follow after coordinator support exists.
2. Multi-process bridge pooling may follow if post-cache benchmarks justify it.
3. JavaScript implementation startup parity may follow as a separate upstream-oriented effort.
4. Extension author manifests may follow if descriptor inference is insufficient for some extension classes.
