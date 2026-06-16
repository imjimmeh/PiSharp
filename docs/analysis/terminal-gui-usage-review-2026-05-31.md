# Terminal.Gui Usage Review (PiSharp)

Date: 2026-05-31
Reviewer: GitHub Copilot (GPT-5.3-Codex)

## Goal

This document reviews all current usage of Terminal.Gui in PiSharp, including:

1. Where Terminal.Gui is used and how those components are linked.
2. How input, commands, dialogs, and extension UI flow through the TUI stack.
3. Architectural risks and complexity hotspots.
4. Concrete improvement paths, including refactoring and potential redesign options.

## Scope And Method

The review covers direct Terminal.Gui usage in source and the supporting CLI wiring and tests.

Repository-wide direct Terminal.Gui usage is currently confined to PiSharp.Tui source files.

- Direct usage inventory command result points to 22 source files under [src/PiSharp.Tui](src/PiSharp.Tui).
- Example host lifecycle and app loop entry: [src/PiSharp.Tui/Interactive/TuiHost.cs](src/PiSharp.Tui/Interactive/TuiHost.cs#L59), [src/PiSharp.Tui/Interactive/TuiHost.cs](src/PiSharp.Tui/Interactive/TuiHost.cs#L72), [src/PiSharp.Tui/Interactive/TuiHost.cs](src/PiSharp.Tui/Interactive/TuiHost.cs#L832), [src/PiSharp.Tui/Interactive/TuiHost.cs](src/PiSharp.Tui/Interactive/TuiHost.cs#L845).
- CLI integration entry point: [src/PiSharp.Cli/Modes/InteractiveMode.cs](src/PiSharp.Cli/Modes/InteractiveMode.cs#L14), [src/PiSharp.Cli/Program.cs](src/PiSharp.Cli/Program.cs#L41).

Primary test surface reviewed is [tests/PiSharp.Tui.Tests](tests/PiSharp.Tui.Tests).

## System Map

### Runtime entry and host composition

1. CLI enters interactive mode in [src/PiSharp.Cli/Modes/InteractiveMode.cs](src/PiSharp.Cli/Modes/InteractiveMode.cs#L14).
2. Interactive mode builds host options in [src/PiSharp.Cli/Modes/InteractiveMode.cs](src/PiSharp.Cli/Modes/InteractiveMode.cs#L18).
3. TUI host creates and wires all core views in [src/PiSharp.Tui/Interactive/TuiHost.cs](src/PiSharp.Tui/Interactive/TuiHost.cs#L101).
4. Render scheduling and frame coalescing handled by [src/PiSharp.Tui/Interactive/TuiRenderScheduler.cs](src/PiSharp.Tui/Interactive/TuiRenderScheduler.cs).
5. State transitions flow through [src/PiSharp.Tui/Interactive/TuiRenderState.cs](src/PiSharp.Tui/Interactive/TuiRenderState.cs) and reducer logic in [src/PiSharp.Tui/Interactive/TuiTranscriptReducer.cs](src/PiSharp.Tui/Interactive/TuiTranscriptReducer.cs).

### Input and shortcut flow

1. App/global key policies are attached in [src/PiSharp.Tui/Interactive/TuiHost.cs](src/PiSharp.Tui/Interactive/TuiHost.cs#L539) and [src/PiSharp.Tui/Interactive/TuiHost.cs](src/PiSharp.Tui/Interactive/TuiHost.cs#L540).
2. Prompt-local key logic is in [src/PiSharp.Tui/Interactive/Components/PromptEditor.cs](src/PiSharp.Tui/Interactive/Components/PromptEditor.cs) and [src/PiSharp.Tui/Interactive/Components/PromptEditorKeyMap.cs](src/PiSharp.Tui/Interactive/Components/PromptEditorKeyMap.cs).
3. Built-in shortcut metadata is in [src/PiSharp.Tui/Interactive/TuiKeybindings.cs](src/PiSharp.Tui/Interactive/TuiKeybindings.cs#L49).
4. Global dispatch and extension shortcut integration are in [src/PiSharp.Tui/Interactive/TuiShortcutRegistrar.cs](src/PiSharp.Tui/Interactive/TuiShortcutRegistrar.cs), [src/PiSharp.Tui/Interactive/TuiShortcutDispatcher.cs](src/PiSharp.Tui/Interactive/TuiShortcutDispatcher.cs), and [src/PiSharp.Tui/Interactive/TuiShortcutController.cs](src/PiSharp.Tui/Interactive/TuiShortcutController.cs).

### Command flow

1. Prompt submission path enters command handling in [src/PiSharp.Tui/Interactive/TuiHost.cs](src/PiSharp.Tui/Interactive/TuiHost.cs#L567).
2. Command control logic is in [src/PiSharp.Tui/Interactive/TuiCommandController.cs](src/PiSharp.Tui/Interactive/TuiCommandController.cs).
3. Slash command registry is built in [src/PiSharp.Cli/Modes/InteractiveMode.cs](src/PiSharp.Cli/Modes/InteractiveMode.cs#L117).
4. Built-in command definitions are in [src/PiSharp.Cli/Commands/BuiltInSlashCommands.cs](src/PiSharp.Cli/Commands/BuiltInSlashCommands.cs#L7).

### Dialog and selection surfaces

1. Generic selector: [src/PiSharp.Tui/Interactive/Components/SelectorDialog.cs](src/PiSharp.Tui/Interactive/Components/SelectorDialog.cs).
2. Session selector: [src/PiSharp.Tui/Interactive/Components/SessionSelectorDialog.cs](src/PiSharp.Tui/Interactive/Components/SessionSelectorDialog.cs).
3. Prompt input dialog wrapper: [src/PiSharp.Tui/Interactive/Components/PromptDialog.cs](src/PiSharp.Tui/Interactive/Components/PromptDialog.cs).
4. Settings and session tree wrappers: [src/PiSharp.Tui/Interactive/Components/SettingsDialog.cs](src/PiSharp.Tui/Interactive/Components/SettingsDialog.cs), [src/PiSharp.Tui/Interactive/Components/SessionTreeDialog.cs](src/PiSharp.Tui/Interactive/Components/SessionTreeDialog.cs).

### Extension UI bridge

1. Bridge host and intent routing: [src/PiSharp.Tui/Interactive/ExtensionUiBridgeHost.cs](src/PiSharp.Tui/Interactive/ExtensionUiBridgeHost.cs).
2. Extension UI adapter: [src/PiSharp.Tui/Interactive/TuiExtensionUi.cs](src/PiSharp.Tui/Interactive/TuiExtensionUi.cs).
3. Host-to-bridge wiring: [src/PiSharp.Tui/Interactive/TuiHost.cs](src/PiSharp.Tui/Interactive/TuiHost.cs#L458).

## Terminal.Gui Usage Inventory

Directly referenced in these source files:

1. [src/PiSharp.Tui/Interactive/TuiHost.cs](src/PiSharp.Tui/Interactive/TuiHost.cs)
2. [src/PiSharp.Tui/Interactive/TuiShortcutRegistrar.cs](src/PiSharp.Tui/Interactive/TuiShortcutRegistrar.cs)
3. [src/PiSharp.Tui/Interactive/TuiShortcutKeyParser.cs](src/PiSharp.Tui/Interactive/TuiShortcutKeyParser.cs)
4. [src/PiSharp.Tui/Interactive/TuiShortcutController.cs](src/PiSharp.Tui/Interactive/TuiShortcutController.cs)
5. [src/PiSharp.Tui/Interactive/TuiKeybindings.cs](src/PiSharp.Tui/Interactive/TuiKeybindings.cs)
6. [src/PiSharp.Tui/Interactive/TuiLayoutMetrics.cs](src/PiSharp.Tui/Interactive/TuiLayoutMetrics.cs)
7. [src/PiSharp.Tui/Interactive/TuiConsoleDriverName.cs](src/PiSharp.Tui/Interactive/TuiConsoleDriverName.cs)
8. [src/PiSharp.Tui/Interactive/ExtensionUiBridgeHost.cs](src/PiSharp.Tui/Interactive/ExtensionUiBridgeHost.cs)
9. [src/PiSharp.Tui/Interactive/Theme/TuiTheme.cs](src/PiSharp.Tui/Interactive/Theme/TuiTheme.cs)
10. [src/PiSharp.Tui/Interactive/Rendering/AnsiStyledText.cs](src/PiSharp.Tui/Interactive/Rendering/AnsiStyledText.cs)
11. [src/PiSharp.Tui/Interactive/Components/ChatView.cs](src/PiSharp.Tui/Interactive/Components/ChatView.cs)
12. [src/PiSharp.Tui/Interactive/Components/PromptEditor.cs](src/PiSharp.Tui/Interactive/Components/PromptEditor.cs)
13. [src/PiSharp.Tui/Interactive/Components/PromptEditorKeyMap.cs](src/PiSharp.Tui/Interactive/Components/PromptEditorKeyMap.cs)
14. [src/PiSharp.Tui/Interactive/Components/SelectorDialog.cs](src/PiSharp.Tui/Interactive/Components/SelectorDialog.cs)
15. [src/PiSharp.Tui/Interactive/Components/SessionSelectorDialog.cs](src/PiSharp.Tui/Interactive/Components/SessionSelectorDialog.cs)
16. [src/PiSharp.Tui/Interactive/Components/PromptDialog.cs](src/PiSharp.Tui/Interactive/Components/PromptDialog.cs)
17. [src/PiSharp.Tui/Interactive/Components/SettingsDialog.cs](src/PiSharp.Tui/Interactive/Components/SettingsDialog.cs)
18. [src/PiSharp.Tui/Interactive/Components/SessionTreeDialog.cs](src/PiSharp.Tui/Interactive/Components/SessionTreeDialog.cs)
19. [src/PiSharp.Tui/Interactive/Components/TuiKeyText.cs](src/PiSharp.Tui/Interactive/Components/TuiKeyText.cs)
20. [src/PiSharp.Tui/Interactive/Components/TuiViewSizing.cs](src/PiSharp.Tui/Interactive/Components/TuiViewSizing.cs)
21. [src/PiSharp.Tui/Interactive/Components/WidthReflowView.cs](src/PiSharp.Tui/Interactive/Components/WidthReflowView.cs)
22. [src/PiSharp.Tui/Interactive/Components/WrappedTextView.cs](src/PiSharp.Tui/Interactive/Components/WrappedTextView.cs)

## Findings (Prioritized)

### Critical / High

#### 1) Session selector performs synchronous wait on the UI loop

Evidence:

- Blocking call inside selector toggle in [src/PiSharp.Tui/Interactive/Components/SessionSelectorDialog.cs](src/PiSharp.Tui/Interactive/Components/SessionSelectorDialog.cs#L181).
- This runs in dialog interaction path via [src/PiSharp.Tui/Interactive/Components/SessionSelectorDialog.cs](src/PiSharp.Tui/Interactive/Components/SessionSelectorDialog.cs#L172).

Why this matters:

1. Freezes the interface while loading all sessions.
2. Can feel like hangs under larger stores.
3. Introduces deadlock risk patterns by mixing sync waiting with async providers.

Recommendation:

1. Replace sync wait with async load kicked from UI event, then apply result via Application.Invoke callback.
2. Show explicit loading state and allow cancellation.
3. Cache all-sessions snapshot with stale-while-revalidate behavior.

#### 2) Footer path blocks render loop with sync wait

Evidence:

- Sync wait in footer snapshot delegate at [src/PiSharp.Cli/Modes/InteractiveMode.cs](src/PiSharp.Cli/Modes/InteractiveMode.cs#L53).
- Footer render invoked from host render in [src/PiSharp.Tui/Interactive/TuiHost.cs](src/PiSharp.Tui/Interactive/TuiHost.cs#L366).

Why this matters:

1. Render thread can stall unpredictably.
2. User perceives sluggish keyboard/mouse responsiveness.
3. Hard to reason about frame pacing and animation consistency.

Recommendation:

1. Move branch retrieval into an async background updater with last-known value cache.
2. Keep footer rendering pure and non-blocking.
3. Add timing instrumentation to detect expensive footer snapshot computations.

#### 3) Prompt input dialog is a non-interactive stub

Evidence:

- No actual prompt widget interaction; returns initial value/empty in [src/PiSharp.Tui/Interactive/Components/PromptDialog.cs](src/PiSharp.Tui/Interactive/Components/PromptDialog.cs#L10).
- Value assignment through invoke in [src/PiSharp.Tui/Interactive/Components/PromptDialog.cs](src/PiSharp.Tui/Interactive/Components/PromptDialog.cs#L16).

Why this matters:

1. Features expecting real user input silently fail open.
2. Command UX appears to work while skipping intended interaction.

Recommendation:

1. Implement real modal input dialog with submit/cancel semantics.
2. Return null on cancel and user text on submit.
3. Add tests for cancellation, default values, and multiline behavior policy.

#### 4) Extension select/confirm/input intents are placeholders, not true UI interactions

Evidence:

- Intent routing in [src/PiSharp.Tui/Interactive/ExtensionUiBridgeHost.cs](src/PiSharp.Tui/Interactive/ExtensionUiBridgeHost.cs#L129), [src/PiSharp.Tui/Interactive/ExtensionUiBridgeHost.cs](src/PiSharp.Tui/Interactive/ExtensionUiBridgeHost.cs#L130), [src/PiSharp.Tui/Interactive/ExtensionUiBridgeHost.cs](src/PiSharp.Tui/Interactive/ExtensionUiBridgeHost.cs#L131).
- Placeholder handlers in [src/PiSharp.Tui/Interactive/ExtensionUiBridgeHost.cs](src/PiSharp.Tui/Interactive/ExtensionUiBridgeHost.cs#L147), [src/PiSharp.Tui/Interactive/ExtensionUiBridgeHost.cs](src/PiSharp.Tui/Interactive/ExtensionUiBridgeHost.cs#L150), [src/PiSharp.Tui/Interactive/ExtensionUiBridgeHost.cs](src/PiSharp.Tui/Interactive/ExtensionUiBridgeHost.cs#L153).

Why this matters:

1. Extension contracts are only partially honored in TUI.
2. Extensions needing explicit user confirmation cannot be correctly implemented.
3. Can create confusing behavior divergence across environments.

Recommendation:

1. Route extension intents through the same real dialog service used by core commands.
2. Enforce explicit timeout/cancel/error return contracts.
3. Add extension parity tests for notify/select/confirm/input paths.

### Medium

#### 5) Selection copy trims row text and can destroy meaningful whitespace

Evidence:

- Selection extraction trims text in [src/PiSharp.Tui/Interactive/Components/ChatView.cs](src/PiSharp.Tui/Interactive/Components/ChatView.cs#L568).

Why this matters:

1. Copied code blocks and diffs lose indentation.
2. Terminal transcript fidelity is reduced.

Recommendation:

1. Preserve raw row text for clipboard selection.
2. If desired, provide separate compact-copy command rather than mutating default selection behavior.

#### 6) Slash command registry is rebuilt repeatedly

Evidence:

- Execute path rebuild in [src/PiSharp.Cli/Modes/InteractiveMode.cs](src/PiSharp.Cli/Modes/InteractiveMode.cs#L30).
- Complete path rebuild in [src/PiSharp.Cli/Modes/InteractiveMode.cs](src/PiSharp.Cli/Modes/InteractiveMode.cs#L81).

Why this matters:

1. Repeated allocations and repeated extension/skills command registration work.
2. Harder to introduce hot-reload policy cleanly.

Recommendation:

1. Build once per runtime instance.
2. Rebuild only on explicit extension/skills catalog mutation event.

#### 7) Extension shortcut bindings rebuilt in dispatch path

Evidence:

- Controller passes builder function in [src/PiSharp.Tui/Interactive/TuiShortcutController.cs](src/PiSharp.Tui/Interactive/TuiShortcutController.cs#L62).
- Registrar invokes source on key dispatch in [src/PiSharp.Tui/Interactive/TuiShortcutRegistrar.cs](src/PiSharp.Tui/Interactive/TuiShortcutRegistrar.cs#L123).

Why this matters:

1. Potential repeated parse/validation overhead on input-heavy usage.
2. Conflict reporting can become noisy or timing-sensitive.

Recommendation:

1. Build immutable binding cache once.
2. Recompute only when extension shortcut registry version changes.

#### 8) Input handling is spread across several layers and subscriptions

Evidence:

- Multiple host-level key subscriptions in [src/PiSharp.Tui/Interactive/TuiHost.cs](src/PiSharp.Tui/Interactive/TuiHost.cs#L539), [src/PiSharp.Tui/Interactive/TuiHost.cs](src/PiSharp.Tui/Interactive/TuiHost.cs#L540), and shortcut registration at [src/PiSharp.Tui/Interactive/TuiHost.cs](src/PiSharp.Tui/Interactive/TuiHost.cs#L756).

Why this matters:

1. Harder to maintain precedence guarantees.
2. Increases regression risk when adding/changing shortcuts.

Recommendation:

1. Consolidate all key events through a single input router with explicit ordered stages.
2. Keep stage rules declarative and testable.

#### 9) TuiHost currently carries too many concerns

Evidence:

- Host class centrality begins at [src/PiSharp.Tui/Interactive/TuiHost.cs](src/PiSharp.Tui/Interactive/TuiHost.cs#L59).
- Render logic, event wiring, extension bridge, command wiring, and snapshot updates are all mixed in one class.

Why this matters:

1. Higher cognitive load and larger blast radius per change.
2. More difficult unit testing and reuse.

Recommendation:

1. Split into host shell, layout composer, input coordinator, command/session coordinator, and extension-ui coordinator.
2. Keep only lifecycle orchestration in TuiHost.

### Low / Observability Gaps

#### 10) Dialog behavior test coverage is narrower than view rendering coverage

Evidence:

- Selector render-window coverage exists in [tests/PiSharp.Tui.Tests/TuiRenderingTests.cs](tests/PiSharp.Tui.Tests/TuiRenderingTests.cs#L1278).
- No strong end-to-end interaction tests were found for prompt dialog and session selector real modal flow in the TUI loop.

Recommendation:

1. Add integration-style tests for dialog submit/cancel and async loading transitions.
2. Add instrumentation for dialog open duration and load latency.

## Strengths Observed

1. Terminal.Gui usage is cleanly scoped to PiSharp.Tui source; no broad leakage into unrelated projects.
2. Keyboard governance and shortcut metadata are explicit and test-covered in [tests/PiSharp.Tui.Tests/TuiShortcutTests.cs](tests/PiSharp.Tui.Tests/TuiShortcutTests.cs).
3. Prompt editor has robust behavior coverage including bracketed paste and cursor logic in [tests/PiSharp.Tui.Tests/PromptEditorTests.cs](tests/PiSharp.Tui.Tests/PromptEditorTests.cs).
4. Rendering decomposition via chat row pipeline and text wrappers is a good foundation for incremental refactor.

## Architectural Improvement Options

### Option A: Incremental hardening (recommended immediate path)

Timeline: short-term

1. Remove synchronous waits from selector and footer paths.
2. Implement true prompt/select/confirm dialogs.
3. Preserve whitespace in selection copy.
4. Cache command registry and shortcut bindings.

Pros:

1. Low migration risk.
2. Quick UX gains and lower freeze risk.

Cons:

1. TuiHost complexity remains mostly intact.

### Option B: Layered TUI architecture refactor

Timeline: medium-term

1. Introduce interfaces for InputRouter, DialogService, RenderCoordinator, SessionCoordinator.
2. Move host-local lambdas into dedicated classes with explicit dependencies.
3. Keep existing view classes, but reduce direct cross-talk.

Pros:

1. Better maintainability and testability.
2. Easier extension and feature evolution.

Cons:

1. More code movement and temporary churn.

### Option C: Deeper redesign around declarative UI state graph

Timeline: long-term

1. Centralize all user intents as actions.
2. Move all view updates through deterministic state transition handlers.
3. Host loop becomes mostly event pump plus renderer.

Pros:

1. Strong consistency and easier time-travel/debug reasoning.
2. Best long-term architecture clarity.

Cons:

1. Highest migration cost.
2. Requires careful phased rollout with parity tests.

## Concrete Refactor Backlog

### Phase 1: Stability and correctness

1. Make session scope toggle async and non-blocking in [src/PiSharp.Tui/Interactive/Components/SessionSelectorDialog.cs](src/PiSharp.Tui/Interactive/Components/SessionSelectorDialog.cs).
2. Replace sync footer branch query in [src/PiSharp.Cli/Modes/InteractiveMode.cs](src/PiSharp.Cli/Modes/InteractiveMode.cs#L53) with cached async data source.
3. Preserve selection whitespace in [src/PiSharp.Tui/Interactive/Components/ChatView.cs](src/PiSharp.Tui/Interactive/Components/ChatView.cs#L568).

### Phase 2: Interaction parity

1. Implement real modal input in [src/PiSharp.Tui/Interactive/Components/PromptDialog.cs](src/PiSharp.Tui/Interactive/Components/PromptDialog.cs).
2. Replace placeholder select/confirm/input in [src/PiSharp.Tui/Interactive/ExtensionUiBridgeHost.cs](src/PiSharp.Tui/Interactive/ExtensionUiBridgeHost.cs) with DialogService integration.
3. Add parity tests under [tests/PiSharp.Tui.Tests](tests/PiSharp.Tui.Tests).

### Phase 3: Performance and maintainability

1. Cache slash command registry in [src/PiSharp.Cli/Modes/InteractiveMode.cs](src/PiSharp.Cli/Modes/InteractiveMode.cs).
2. Add shortcut binding cache + invalidation in [src/PiSharp.Tui/Interactive/TuiShortcutController.cs](src/PiSharp.Tui/Interactive/TuiShortcutController.cs).
3. Split host responsibilities in [src/PiSharp.Tui/Interactive/TuiHost.cs](src/PiSharp.Tui/Interactive/TuiHost.cs).

## Suggested Target Module Decomposition

1. TuiHostShell: app init/run/shutdown, terminal session enter/exit.
2. TuiLayoutComposer: view construction and constraints.
3. TuiInputRouter: all key and mouse routing with explicit precedence.
4. TuiDialogService: select/confirm/input/message wrappers.
5. TuiCommandSessionCoordinator: slash command dispatch, session changes, snapshot refresh.
6. TuiExtensionUiCoordinator: bridge setup, intent handling, extension status/widgets.
7. TuiRenderCoordinator: render scheduling and redraw policies.

## File-Level Notes

### TuiHost

- Strong functionality density in [src/PiSharp.Tui/Interactive/TuiHost.cs](src/PiSharp.Tui/Interactive/TuiHost.cs).
- Key opportunity: move nested local functions into composable collaborators.

### PromptEditor

- Good input normalization and bracketed paste handling in [src/PiSharp.Tui/Interactive/Components/PromptEditor.cs](src/PiSharp.Tui/Interactive/Components/PromptEditor.cs).
- Opportunity: isolate cursor mapping helpers into dedicated utility for focused tests and reuse.

### ChatView

- Rich interaction and selection handling in [src/PiSharp.Tui/Interactive/Components/ChatView.cs](src/PiSharp.Tui/Interactive/Components/ChatView.cs).
- Opportunity: separate interaction state machine from rendering concerns.

### SelectorDialog and SessionSelectorDialog

- Substantial duplicated structure and event flow between [src/PiSharp.Tui/Interactive/Components/SelectorDialog.cs](src/PiSharp.Tui/Interactive/Components/SelectorDialog.cs) and [src/PiSharp.Tui/Interactive/Components/SessionSelectorDialog.cs](src/PiSharp.Tui/Interactive/Components/SessionSelectorDialog.cs).
- Opportunity: shared generic searchable list dialog infrastructure.

### ExtensionUiBridgeHost

- Good bridge slot/status plumbing in [src/PiSharp.Tui/Interactive/ExtensionUiBridgeHost.cs](src/PiSharp.Tui/Interactive/ExtensionUiBridgeHost.cs).
- Opportunity: finish interaction parity and explicit behavior contracts for each intent kind.

## Risk Register

1. UI freeze risk from sync waits.
2. Behavior parity gaps for extension and command user interaction.
3. Input precedence regressions as keybinding complexity grows.
4. Continued host growth increasing maintenance drag.

## Decision Guidance

If the goal is fastest user-visible improvement, do Option A now, then Option B.

If the goal is strongest long-term architecture-first posture, begin Option B immediately while cherry-picking Option A blocking fixes first.

## Appendix: Related Tests

Key test files reviewed:

1. [tests/PiSharp.Tui.Tests/TuiRenderingTests.cs](tests/PiSharp.Tui.Tests/TuiRenderingTests.cs)
2. [tests/PiSharp.Tui.Tests/TuiPerformanceTests.cs](tests/PiSharp.Tui.Tests/TuiPerformanceTests.cs)
3. [tests/PiSharp.Tui.Tests/PromptEditorTests.cs](tests/PiSharp.Tui.Tests/PromptEditorTests.cs)
4. [tests/PiSharp.Tui.Tests/TuiShortcutTests.cs](tests/PiSharp.Tui.Tests/TuiShortcutTests.cs)
5. [tests/PiSharp.Tui.Tests/TuiShortcutControllerTests.cs](tests/PiSharp.Tui.Tests/TuiShortcutControllerTests.cs)
6. [tests/PiSharp.Tui.Tests/ExtensionUiBridgeHostTests.cs](tests/PiSharp.Tui.Tests/ExtensionUiBridgeHostTests.cs)
7. [tests/PiSharp.Tui.Tests/TuiConsoleDriverNameTests.cs](tests/PiSharp.Tui.Tests/TuiConsoleDriverNameTests.cs)
