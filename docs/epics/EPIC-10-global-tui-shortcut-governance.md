---
epic_id: EPIC-10
title: Global TUI Shortcut Governance And Command Ownership Refactor
status: completed
priority: high
owner: unassigned
created: 2026-05-27
updated: 2026-05-27
target_version: backlog
related_docs:
  - ../specs/PRD-pi-csharp-port.md
  - ../specs/SDD-pi-csharp-port.md
  - ./EPIC-05-cli-and-modes.md
  - ./EPIC-09-interactive-tui-visual-parity.md
related_code:
  - ../../src/PiSharp.Tui/Interactive/TuiHost.cs
  - ../../src/PiSharp.Tui/Interactive/TuiKeybindings.cs
  - ../../src/PiSharp.Tui/Interactive/Components/PromptEditor.cs
  - ../../src/PiSharp.Tui/Interactive/Components/PromptEditorController.cs
  - ../../src/PiSharp.Tui/Interactive/Components/PromptEditorKeyMap.cs
  - ../../src/PiSharp.Tui/Interactive/Components/PromptEditorMode.cs
  - ../../src/PiSharp.Tui/Interactive/Components/PromptHistory.cs
  - ../../src/PiSharp.Tui/Interactive/Components/PromptSuggestionState.cs
  - ../../src/PiSharp.Tui/Interactive/Components/HeaderView.cs
  - ../../tests/PiSharp.Tui.Tests/TuiRenderingTests.cs
decision_summary: Finish the post-c523c95 shortcut ownership refactor by moving all app-level shortcut registration and dispatch to host initialization, using injected command/action objects, and reducing PromptEditor to editor-only input semantics.
tags:
  - tui
  - keyboard
  - architecture
  - terminal-gui
  - refactor
  - reliability
---

# EPIC-10: Global TUI Shortcut Governance And Command Ownership Refactor

## 1. Background And Context

### 1.1 Current State After `c523c95`

PiSharp interactive mode now has a partially improved prompt stack, but shortcut ownership is still mixed.

Completed preparatory work:

1. `PromptEditor` delegates most prompt behavior to `PromptEditorController`.
2. Prompt history is extracted to `PromptHistory`.
3. Suggestion state is extracted to `PromptSuggestionState`.
4. Normal prompt vs inline selection behavior is represented by `PromptEditorMode` types.
5. Prompt key routing is isolated behind `PromptEditorKeyMap`.

Remaining ownership problems:

1. `PromptEditor` still constructs its own controller and keymap instead of receiving behavior dependencies.
2. `PromptEditor` still registers app-level Terminal.Gui commands for Esc, Ctrl+D, Ctrl+L, Ctrl+R, Ctrl+O, Ctrl+T, Ctrl+H, and Ctrl+C/Ctrl+U.
3. `PromptEditorController` still exposes app-specific events such as model selection, session tree, tool toggle, thinking toggle, header toggle, abort, and exit.
4. `TuiHost` still wires app behavior through PromptEditor events instead of a host-owned shortcut dispatcher.
5. `TuiKeybindings` describes visible shortcut metadata, but active shortcut registration is not derived from the same source.
6. Transcript scroll shortcuts and prompt focus routing remain separate host-level handlers, not part of a coherent shortcut governance model.

### 1.2 Why This Matters

The current split still increases risk in four ways:

1. Terminal.Gui default command leakage can reappear when a view is changed or replaced.
2. Behavioral ownership is harder to reason about because registration, metadata, and execution are spread across `PromptEditor`, `PromptEditorKeyMap`, `TuiHost`, and `TuiKeybindings`.
3. Future features such as mode-specific overrides, extension-aware shortcuts, or configurable keymaps will be brittle without a first-class action/command model.
4. `PromptEditor` remains a product-specific app command router instead of a reusable editor component.

### 1.3 Triggering Incident

A Ctrl+L crash exposed inherited Terminal.Gui `TextView` command execution (`PromptForColors`) being invoked from command bindings rather than intended app behavior. The local fix and `c523c95` restored stability and extracted some prompt internals, but the architectural ownership concern remains.

## 2. Problem Statement

Refactor interactive TUI input architecture so that:

1. App-level shortcut bindings are established during TuiHost initialization through a centralized registrar.
2. App-level command execution is managed by a host-owned dispatcher with injectable command handlers.
3. PromptEditor owns only editor semantics and receives any editor command policy through dependencies.
4. Keybinding metadata, active registration, help text, and header hints stay synchronized.
5. Tests prove governed keys cannot fall through to unsafe Terminal.Gui defaults.

## 3. Goals And Non-Goals

### 3.1 Goals

1. Introduce a typed `TuiShortcutAction` model for every governed shortcut.
2. Introduce a host-owned `TuiShortcutDispatcher` that is built from injected command handlers.
3. Introduce a centralized `TuiShortcutRegistrar` invoked from `TuiHost` startup.
4. Convert `TuiKeybindings` from display-only metadata into the source for governed key definitions where practical.
5. Move app command handling out of `PromptEditor` and `PromptEditorController`.
6. Keep PromptEditor responsible for editor-only semantics: submit, completion, history, suggestions, caret movement, insertion/deletion, and inline-selection editing behavior.
7. Preserve existing UX behavior and displayed keybinding hints.
8. Add regression tests proving governed shortcut keys no longer execute unwanted default Terminal.Gui commands.

### 3.2 Non-Goals

1. Replacing Terminal.Gui input subsystem.
2. Redesigning default shortcut UX or changing shortcut keys in this epic.
3. Building user-configurable keymaps or persisted remapping; this should become a follow-up epic once ownership is clean.
4. Building extension-owned keybinding injection; this should become a follow-up epic.
5. Refactoring non-interactive CLI mode input paths.
6. Rewriting slash-command dispatch beyond what is required to keep shortcut ownership clean.

## 4. Constraints And Assumptions

### 4.1 Technical Constraints

1. Terminal.Gui provides built-in key and command maps that may execute before ad-hoc `KeyDown` logic depending on binding state.
2. Text editing defaults cannot simply be disabled wholesale; PromptEditor still needs explicit caret/editing semantics.
3. App-level shortcuts need to work whether focus is in the prompt, transcript, or another normal interactive view.
4. Dialogs and inline selection may need local input behavior and should not accidentally inherit global app shortcuts that conflict with dialog semantics.
5. Integration tests are limited by terminal runtime characteristics and should prefer deterministic in-process event simulation where possible.

### 4.2 Product Constraints

1. Existing documented shortcuts in `TuiKeybindings` must remain accurate.
2. Interactive mode behavior must remain stable across Windows terminal environments.
3. No regressions in prompt typing, prompt history, completion, inline selection, focus routing, or transcript scrolling ergonomics.
4. `/help` and header hints must describe actual active bindings.

### 4.3 Design Assumptions

1. `TuiHost` is the correct orchestration boundary for app-level commands.
2. `PromptEditor` should remain reusable as an editor component with no PiSharp app command knowledge.
3. Shortcut metadata should remain centralized and discoverable for help/header rendering.
4. Command handlers should be dependency-injected as a collection/map so future shortcuts do not require editing PromptEditor or a large host switch statement.
5. Host command side effects can still close over local TuiHost state, but the mapping from action to side effect must be explicit and testable.

## 5. Current Gap Matrix

### 5.1 Ownership Gap

`PromptEditor` and `PromptEditorController` still contain app command registrations and app-specific events, while `TuiHost` performs the actual side effects. This means the prompt remains both editor and app shortcut adapter.

### 5.2 Dependency-Injection Gap

`PromptEditor` constructs `PromptEditorController` and `PromptEditorKeyMap` directly. This makes command behavior harder to test in isolation and prevents the host from choosing a mode-specific or stripped-down keymap.

### 5.3 Initialization Gap

There is no single initialization module that configures all interactive mode shortcuts in one place at startup. Governed shortcuts are split between PromptEditor bindings, PromptEditor keymap logic, and TuiHost global handlers.

### 5.4 Metadata Synchronization Gap

`TuiKeybindings.Defaults` is currently display metadata. It does not drive active registration, so metadata and behavior can drift.

### 5.5 Test Coverage Gap

Current regression coverage proves selected PromptEditor key paths do not crash and editing behavior works, but does not enforce host-level ownership boundaries or metadata/registration synchronization.

## 6. Target State

At epic completion, interactive mode should satisfy:

1. A host-initialized shortcut registrar governs app shortcuts globally.
2. A host-owned dispatcher maps typed action IDs to injected command handlers.
3. `TuiKeybindings` or a sibling metadata type is the single source for displayed keys and governed registration definitions.
4. `PromptEditor` owns only editor behavior and no app command registry.
5. `PromptEditorController` emits only editor-domain events, such as submitted prompt and suggestions changed.
6. Host-owned commands may call narrow PromptEditor methods such as `ClearPrompt`, `FocusAtEnd`, or `MovePromptHistory`, but PromptEditor does not know what app action caused the call.
7. Transcript scrolling, prompt focus routing, and app-level shortcuts are either in the same registrar/dispatcher or explicitly documented as separate lower-level input policies.
8. Shortcut hints and `/help` output remain accurate and sourced from centralized definitions.
9. Deterministic tests guard against Terminal.Gui default-command regressions.

## 7. PR-Sized Task Breakdown

### 7.1 Task 1: Define Shortcut Metadata, Action Model, And Scopes

Scope:

1. Add a typed action model, for example `TuiShortcutAction`, for governed actions.
2. Add shortcut scope metadata, for example global app, prompt editor, transcript navigation, and dialog-local.
3. Move `TuiKeybindings` toward structured metadata that includes keys, description, action, scope, and registration policy.
4. Keep existing `HotkeysText()` and `HintText()` behavior working during transition.

Primary files:

- ../../src/PiSharp.Tui/Interactive/TuiKeybindings.cs
- ../../tests/PiSharp.Tui.Tests/TuiRenderingTests.cs

Definition of Done:

1. Every currently documented shortcut has a typed action or an explicit reason it remains display-only.
2. Existing help/header hint output remains unchanged.
3. Tests enforce keybinding metadata shape and key string preservation.

Acceptance Criteria:

1. New shortcuts can be described without changing PromptEditor.
2. The metadata can be consumed by a registrar in later tasks.

Tests:

1. Keybinding metadata tests for action uniqueness, key preservation, and help formatting.

### 7.2 Task 2: Introduce Injectable Host Shortcut Dispatcher

Scope:

1. Add a host-level shortcut command interface, for example `ITuiShortcutCommand`.
2. Implement `TuiShortcutDispatcher` from an injected command collection or action-to-command map.
3. Define a `TuiShortcutContext` carrying narrow host capabilities needed by commands, such as prompt operations, transcript scroll operations, state update, command dispatch, abort, and exit.
4. Implement command handlers for app-level actions: abort, exit, clear editor, model selector, session tree, tool output toggle, thinking toggle, and header toggle.

Primary files:

- ../../src/PiSharp.Tui/Interactive/TuiHost.cs
- ../../src/PiSharp.Tui/Interactive/ (new dispatcher/command/context files)
- ../../tests/PiSharp.Tui.Tests/ (new shortcut dispatcher test file if needed)

Definition of Done:

1. App actions map through a single dispatcher.
2. Dispatcher behavior is unit-testable without Terminal.Gui runtime.
3. Host command handlers are injectable or replaceable in tests.

Acceptance Criteria:

1. No app behavior needs to be executed directly inside PromptEditor shortcut handlers.
2. Dispatcher logic preserves current runtime outcomes.

Tests:

1. Unit tests for action-to-side-effect routing using fake context/capabilities.
2. Negative test for unknown/unregistered action behavior.

### 7.3 Task 3: Add Host-Level Shortcut Registrar On App Init

Scope:

1. Create a dedicated registrar that configures keybindings during `TuiHost` setup.
2. Register app shortcuts against host dispatch actions.
3. Neutralize unsafe inherited default bindings for governed keys.
4. Decide and document whether transcript scroll shortcuts and prompt focus routing are first-class registrar entries or separate input policies.
5. Ensure global app shortcuts work when focus is in PromptEditor or normal transcript areas.

Primary files:

- ../../src/PiSharp.Tui/Interactive/TuiHost.cs
- ../../src/PiSharp.Tui/Interactive/ (new registrar file)
- ../../tests/PiSharp.Tui.Tests/ (host shortcut tests)

Definition of Done:

1. App shortcut registration occurs in one startup path.
2. Registration is explicit and traceable to keybinding metadata.
3. Governed app shortcuts do not depend on PromptEditor registering app command constants.

Acceptance Criteria:

1. Esc, Ctrl+D, Ctrl+C/Ctrl+U, Ctrl+L, Ctrl+R, Ctrl+O, Ctrl+T, and Ctrl+H behave exactly as intended.
2. No observed fallback into unintended Terminal.Gui defaults for app shortcuts.
3. Global app shortcuts are not accidentally active in modal dialogs where they would conflict with dialog behavior.

Tests:

1. Focused tests exercising key events through host-bound path.
2. Regression test for Ctrl+L not invoking `TextView` color prompt behavior.

### 7.4 Task 4: Simplify PromptEditor To Editor Semantics And Inject Editor Policy

Scope:

1. Remove app command constants, app command registrations, and app-specific events from `PromptEditor`.
2. Remove app-specific events from `PromptEditorController`.
3. Keep or introduce editor-only events/callbacks: submitted prompt, suggestions changed, and maybe cleared if needed as an editor-domain event.
4. Inject `PromptEditorController`, `PromptEditorKeyMap`, or editor command definitions instead of constructing all behavior internally.
5. Keep editor-only functionality: submit, completion, history, suggestions, inline-selection mode, caret movement, deletion, insertion, and editor-local Up/Down/Tab/Enter handling.
6. Provide narrow public operations host commands can call, for example `ClearPrompt()`, `SetPromptText()`, `InsertAtEnd()`, `FocusAtEnd()`, and `MovePromptHistory()`.

Primary files:

- ../../src/PiSharp.Tui/Interactive/Components/PromptEditor.cs
- ../../src/PiSharp.Tui/Interactive/Components/PromptEditorController.cs
- ../../src/PiSharp.Tui/Interactive/Components/PromptEditorKeyMap.cs
- ../../src/PiSharp.Tui/Interactive/TuiHost.cs

Definition of Done:

1. `PromptEditor` has no app-specific command constants for host concerns.
2. `PromptEditorController` has no model/tree/tools/thinking/header/exit/abort events.
3. PromptEditor behavior dependencies can be injected by tests or host construction.
4. PromptEditor remains fully functional for editing workflows.

Acceptance Criteria:

1. Prompt history, completion, submit, inline selection, and multiline behavior remain unchanged.
2. App command behavior is still available through host/global bindings.
3. PromptEditor code reads as an editor component rather than an app command router.

Tests:

1. Existing PromptEditor behavior tests remain green.
2. New tests validate no loss of editing semantics after app event removal.
3. Constructor/injection tests prove editor command behavior can be substituted without Terminal.Gui app actions.

### 7.5 Task 5: Align Keybinding Metadata, Help, And Header Hints

Scope:

1. Ensure help text and header hints render from the same structured definitions used by the registrar.
2. Remove duplicate hard-coded key strings for governed actions.
3. Add metadata synchronization checks so every registered action has a display entry and every displayed governed action is registered.

Primary files:

- ../../src/PiSharp.Tui/Interactive/TuiKeybindings.cs
- ../../src/PiSharp.Tui/Interactive/Components/HeaderView.cs
- ../../src/PiSharp.Tui/Interactive/TuiHost.cs
- ../../src/PiSharp.Tui/Interactive/ (registrar/metadata files)

Definition of Done:

1. Help text and header hints render from shared definitions.
2. No duplicate hard-coded key strings remain for governed actions.
3. Metadata and active registration cannot drift silently.

Acceptance Criteria:

1. `/help` hotkey section reflects actual active bindings.
2. Header hints remain correct after refactor.

Tests:

1. Keybinding hint formatting tests.
2. Metadata/registrar synchronization tests.

### 7.6 Task 6: Unify Or Document Transcript Scroll And Prompt Focus Routing

Scope:

1. Evaluate existing `TuiHost` handlers for PageUp/PageDown/End/Ctrl+Up/Ctrl+Down and global printable-character prompt focus.
2. Either move transcript scroll shortcuts into the shortcut registrar/dispatcher or document them as a separate host input policy with tests.
3. Keep prompt focus routing behavior unchanged: printable global input focuses the prompt and inserts text; Up/Down while prompt is unfocused navigates prompt history.

Primary files:

- ../../src/PiSharp.Tui/Interactive/TuiHost.cs
- ../../src/PiSharp.Tui/Interactive/ (registrar/input policy files)
- ../../tests/PiSharp.Tui.Tests/ (host input policy tests)

Definition of Done:

1. Reviewer can identify where transcript scroll shortcuts and global prompt focus routing live.
2. These policies do not conflict with app-level shortcut registration.
3. Behavior is covered by deterministic tests where practical.

Acceptance Criteria:

1. PageUp/PageDown, End, and Ctrl+Up/Ctrl+Down continue scrolling transcript.
2. Printable keys still focus and insert into the prompt when appropriate.
3. Ctrl-modified app shortcuts are not swallowed by prompt focus routing.

Tests:

1. Host input policy tests for scroll routing and prompt focus routing.

### 7.7 Task 7: Regression Suite Hardening For Shortcut Safety

Scope:

1. Keep existing no-crash tests for Ctrl+L/Ctrl+R/Ctrl+O until replaced by stronger host-level tests.
2. Add host-level regression tests for global init behavior.
3. Add at least one negative test ensuring default `TextView` color-prompt path is not used for governed keys.
4. Add tests proving PromptEditor has no app-level shortcut registrations after simplification.

Primary files:

- ../../tests/PiSharp.Tui.Tests/TuiRenderingTests.cs
- ../../tests/PiSharp.Tui.Tests/ (new host shortcut test file if needed)

Definition of Done:

1. Test suite fails if governed keys regress to inherited default command behavior.
2. Test suite fails if PromptEditor reintroduces app-level shortcut ownership.
3. Test suite passes with deterministic execution.

Acceptance Criteria:

1. Reproduced crash class is permanently guarded.
2. Future shortcut changes require explicit test updates.

Tests:

1. Unit and integration-style TUI tests.

## 8. Cross-Cutting Definition Of Done (Epic Level)

The epic is complete only when all of the following are true:

1. Shortcut registration ownership is centralized in host initialization.
2. App command side effects are centralized behind an injectable host dispatcher.
3. PromptEditor no longer owns app command orchestration.
4. PromptEditor behavior dependencies are injectable or otherwise replaceable in tests.
5. Help/header metadata and active bindings are consistent.
6. Existing interactive behavior remains stable.
7. Regression tests cover the previous crash vector and host-level paths.
8. `dotnet format PiSharp.sln --verify-no-changes --no-restore` passes.
9. `dotnet test PiSharp.sln --no-restore` passes.

## 9. Acceptance Criteria (Epic Level)

1. Reviewer can identify one location for governed shortcut registration and one location for shortcut dispatch.
2. Reviewer can identify which input policies remain editor-local and why.
3. PromptEditor code reads as an editor component rather than app command router.
4. Ctrl shortcut behavior matches published keybinding hints.
5. No-crash guarantees for governed keys are test-proven.
6. Metadata/registration synchronization is test-proven.
7. Solution formatting and tests remain green.

## 10. Test Strategy

### 10.1 Unit Tests

1. Shortcut action dispatch mapping and outcomes.
2. Injectable command handler behavior using fake host context/capabilities.
3. Keybinding metadata synchronization checks.
4. PromptEditorController editor behavior without app command events.
5. PromptEditor keymap/editor command injection behavior where practical.

### 10.2 Interaction Tests

1. Governed key events routed through host-level path.
2. Prompt editor editing semantics unchanged.
3. Transcript scroll shortcuts unchanged.
4. Global prompt focus routing unchanged.

### 10.3 Regression Tests

1. Ctrl+L/Ctrl+R/Ctrl+O no-crash tests remain mandatory until superseded by host-level no-fallback tests.
2. Additional checks for Esc/Ctrl+D/Ctrl+C/Ctrl+U behavioral stability.
3. Negative checks for unsafe inherited Terminal.Gui default command paths.
4. Boundary checks proving PromptEditor does not contain app-level shortcut registration.

### 10.4 Build And CI Guardrails

1. `dotnet format PiSharp.sln --verify-no-changes --no-restore` must pass.
2. `dotnet test PiSharp.sln --no-restore` must pass.
3. At minimum, `tests/PiSharp.Tui.Tests` must pass after every task slice.

## 11. Risks And Mitigations

1. Risk: Clearing bindings too broadly can break expected editing defaults.
   Mitigation: Scope registrar changes to governed app shortcuts and keep editor semantics explicit.

2. Risk: Duplicate registration logic between host and metadata definitions.
   Mitigation: Add a single translation layer from metadata action IDs to registration and dispatch actions, plus synchronization tests.

3. Risk: Refactor introduces focus regressions.
   Mitigation: Add focused interaction tests for typing, history navigation, and scroll shortcuts.

4. Risk: Subtle platform-specific key behavior differences.
   Mitigation: Keep key definitions centralized and validate on Windows terminal environment as baseline.

5. Risk: Dispatcher abstraction becomes a large switch statement in disguise.
   Mitigation: Prefer injected command handlers and a command collection/map over hard-coded PromptEditor or host branches.

6. Risk: Dialog-local input behavior conflicts with global shortcuts.
   Mitigation: Include shortcut scopes and modal/dialog exclusion rules in metadata/registrar design.

## 12. Dependencies

1. EPIC-05 for interactive mode architecture and slash command behavior.
2. EPIC-09 for key hint parity expectations and TUI visual contract continuity.
3. Existing PiSharp.Tui.Tests infrastructure for deterministic input simulation.
4. `c523c95` prompt extraction baseline.

## 13. Out Of Scope / Follow-Ups

1. User-customizable keymaps and persisted remapping.
2. Extension-owned keybinding injection model.
3. Broader Terminal.Gui abstraction layer beyond this ownership refactor.
4. Changing default key choices.
5. Replacing slash-command parsing/dispatch.

## 14. Implementation Notes For Reviewers

1. Prioritize ownership clarity and deterministic behavior over broad stylistic refactors.
2. Keep each PR narrow: metadata/action model, dispatcher, registrar, PromptEditor simplification, host input policy cleanup, and tests can be separate.
3. Validate that shortcut behavior and help text remain aligned in every PR.
4. Treat `c523c95` as a preparatory extraction, not as completion of shortcut governance.
5. App-level commands do not belong in PromptEditor even if they are triggered while PromptEditor has focus.
6. Clear editor should be host-owned as a governed shortcut, with the host invoking a narrow editor operation.

## 15. Suggested Milestone Plan

1. Milestone A: metadata/action model + test-preserved help output.
2. Milestone B: injectable host dispatcher + command handlers.
3. Milestone C: host-level registrar and app shortcut wiring.
4. Milestone D: PromptEditor simplification and injected editor policy.
5. Milestone E: transcript/focus input policy cleanup.
6. Milestone F: regression hardening and final metadata synchronization.

## 16. Implementation Checklist (Per-PR Template)

Use this checklist in every PR linked to this epic. Copy into the PR description and fill it out.

### 16.1 Scope And Traceability

- [ ] PR title follows: `EPIC-10 / Task X / <area>`
- [ ] PR links to EPIC-10 and specific task number(s) from Section 7
- [ ] PR states whether it is `feature`, `refactor-only`, or `test-only`
- [ ] PR includes a short "out of scope" list
- [ ] PR lists the exact files changed

### 16.2 Ownership Integrity

- [ ] Shortcut registration changes occur in host init path
- [ ] PromptEditor does not gain new app command responsibilities
- [ ] Action dispatch path is explicit and testable
- [ ] Behavior side effects are centralized
- [ ] Command handlers are injectable or otherwise replaceable in tests
- [ ] PromptEditor dependencies are injectable where this PR touches editor construction

### 16.3 Shortcut Coverage Checklist

Mark only the shortcuts touched by the PR.

- [ ] Enter (submit prompt)
- [ ] Shift+Enter (newline)
- [ ] Tab (accept completion)
- [ ] Up/Down (prompt history/suggestion navigation)
- [ ] Esc (abort)
- [ ] Ctrl+D (exit)
- [ ] Ctrl+C/Ctrl+U (clear editor)
- [ ] Ctrl+L (model selector)
- [ ] Ctrl+R (session tree)
- [ ] Ctrl+O (tool output toggle)
- [ ] Ctrl+T (thinking toggle)
- [ ] Ctrl+H (header toggle)
- [ ] PageUp/PageDown (transcript scroll)
- [ ] Ctrl+Up/Ctrl+Down (line scroll)
- [ ] End (scroll to latest)
- [ ] Printable global input (focus prompt and insert)

### 16.4 Test Coverage Checklist

- [ ] Added or updated unit tests for changed dispatch/registration logic
- [ ] Added or updated interaction tests for governed keys
- [ ] Added or updated metadata/help/header synchronization tests if metadata changed
- [ ] Verified no-crash regression tests remain green
- [ ] Existing related tests remain green

### 16.5 Manual Verification Checklist

- [ ] Verified interactive mode starts with expected shortcuts active
- [ ] Verified help hotkeys text matches observed behavior
- [ ] Verified prompt typing/history/completion behavior remains intact
- [ ] Verified inline selection behavior remains intact
- [ ] Verified scroll shortcuts remain intact
- [ ] Verified governed shortcuts do not fire unexpectedly in modal dialogs

### 16.6 Quality Gates

- [ ] No unrelated refactors mixed into PR
- [ ] No dead keybinding constants or duplicate mappings introduced
- [ ] Naming aligns with existing conventions
- [ ] Reviewer can validate behavior from PR description without deep source-diving
- [ ] `dotnet format PiSharp.sln --verify-no-changes --no-restore` passes
- [ ] Relevant tests pass; final epic PR runs `dotnet test PiSharp.sln --no-restore`

### 16.7 Definition Of Done Confirmation (Per PR)

- [ ] Task-specific DoD items from Section 7 are complete
- [ ] Task-specific acceptance criteria from Section 7 are satisfied
- [ ] Test expectations from Section 7 are satisfied
- [ ] Any remaining follow-up work is captured as a concrete TODO issue/task

### 16.8 PR Summary Template

Use this exact structure in PR descriptions:

```md
## EPIC-10 Task Mapping

- Task: 7.X
- Type: feature | refactor-only | test-only

## What Changed

- ...

## Ownership Outcome

- Registration location: ...
- Dispatch location: ...
- PromptEditor responsibilities removed/retained: ...
- Dependency injection outcome: ...

## Tests

- Unit: ...
- Interaction: ...
- Manual: ...

## Risks

- ...

## Out Of Scope

- ...
```

### 16.9 Final Epic Exit Checklist

Use at epic close-out in addition to Section 8:

- [ ] All Section 7 tasks are complete or explicitly deferred
- [ ] Deferred items have owner + follow-up issue
- [ ] Host registration and dispatch architecture are documented
- [ ] PromptEditor ownership boundary is documented
- [ ] Metadata/registration synchronization is documented
- [ ] Epic status is updated from `in_progress` to final state

## 17. Implementation Summary

### 17.1 Completed Before This Epic Update

1. Commit `c523c95` extracted prompt behavior into controller/state helpers:
   - `PromptEditorController`
   - `PromptEditorKeyMap`
   - `PromptEditorMode`
   - `PromptHistory`
   - `PromptSuggestionState`
2. Existing PromptEditor tests pass against the extracted structure.
3. The extraction reduced `PromptEditor` size but did not complete shortcut ownership governance.

### 17.2 Current Decisions

1. State pattern remains appropriate for prompt mode behavior such as normal prompt vs inline selection.
2. Command pattern remains appropriate for key routing, but app-level commands must be host-owned and injectable.
3. PromptEditor should retain editor-local commands only.
4. Clear editor is treated as a governed host shortcut, even though its side effect is an editor operation.
5. `TuiKeybindings` should become or feed the metadata source used by both display and registration.

### 17.3 Completed In This Implementation

1. Added structured shortcut metadata with typed actions, scopes, registration policies, and Terminal.Gui keys while preserving existing help/header strings.
2. Added an injectable host shortcut dispatcher and context capabilities for app-level actions.
3. Added a host startup shortcut registrar that resolves governed global shortcuts from metadata.
4. Removed app-level shortcut events, command constants, and command bindings from `PromptEditor` and `PromptEditorController`.
5. Kept prompt editing behavior local to the editor keymap and made the editor keymap injectable.
6. Documented transcript scroll and printable prompt-focus routing as host input policies through metadata rather than global app shortcut registrations.
7. Added regression tests for metadata synchronization, dispatcher routing, registrar resolution, PromptEditor ownership boundaries, and injected editor keymap behavior.

### 17.4 Remaining Follow-Ups

1. User-configurable keymaps and persisted remapping remain out of scope.
2. Extension-owned keybinding injection remains out of scope.
3. Broader Terminal.Gui abstraction work remains out of scope.
