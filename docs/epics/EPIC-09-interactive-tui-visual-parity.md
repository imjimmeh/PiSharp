---
epic_id: EPIC-09
title: Interactive TUI Visual Parity With JavaScript Baseline
status: in-progress
priority: high
owner: unassigned
created: 2026-05-27
updated: 2026-05-27
target_version: backlog
related_docs:
  - ../specs/PRD-pi-csharp-port.md
  - ../specs/SDD-pi-csharp-port.md
  - ./EPIC-05-cli-and-modes.md
  - ./EPIC-06-extension-system-and-ts-bridge.md
related_code:
  - ../../src/PiSharp.Tui/Interactive/TuiHost.cs
  - ../../src/PiSharp.Tui/Interactive/Theme/TuiTheme.cs
  - ../../src/PiSharp.Tui/Interactive/Components/HeaderView.cs
  - ../../src/PiSharp.Tui/Interactive/Components/ChatView.cs
  - ../../src/PiSharp.Tui/Interactive/Components/MessageRenderers.cs
  - ../../src/PiSharp.Tui/Interactive/Components/ToolExecutionView.cs
  - ../../src/PiSharp.Tui/Interactive/Components/PromptEditor.cs
  - ../../src/PiSharp.Tui/Interactive/Components/FooterView.cs
  - ../../src/PiSharp.Tui/Interactive/FooterDataProvider.cs
  - ../../src/PiSharp.Tui/Interactive/Rendering/TuiMarkdownRenderer.cs
  - ../../javascript/packages/coding-agent/src/modes/interactive/interactive-mode.ts
  - ../../javascript/packages/coding-agent/src/modes/interactive/theme/theme.ts
  - ../../javascript/packages/coding-agent/src/modes/interactive/theme/dark.json
  - ../../javascript/packages/coding-agent/src/modes/interactive/components/footer.ts
  - ../../javascript/packages/coding-agent/src/modes/interactive/components/user-message.ts
  - ../../javascript/packages/coding-agent/src/modes/interactive/components/assistant-message.ts
  - ../../javascript/packages/coding-agent/src/modes/interactive/components/tool-execution.ts
  - ../../javascript/packages/coding-agent/src/modes/interactive/components/custom-message.ts
  - ../../javascript/packages/coding-agent/src/modes/interactive/components/dynamic-border.ts
decision_summary: Rework PiSharp base interactive TUI to match the JavaScript interactive baseline as closely as Terminal.Gui and platform constraints allow, using a parity contract, tokenized theme mapping, composition alignment, and snapshot-based regression tests.
tags:
  - tui
  - parity
  - ux
  - theming
  - terminal-gui
  - migration
---

# EPIC-09: Interactive TUI Visual Parity With JavaScript Baseline

## 1. Background And Context

### 1.1 Current State

PiSharp currently provides a functional interactive TUI, but its visual language differs from the JavaScript reference implementation in core areas:

1. Theme/token model
2. Layout composition and spacing
3. Header onboarding and keybinding hints
4. Message rendering styles (user/assistant/tool/custom)
5. Footer information architecture and formatting
6. Border treatment and state color semantics

These differences create a "same product, different feel" outcome and reduce confidence that PiSharp base mode is a faithful port.

### 1.2 Why This Matters

The JavaScript interactive mode is the de facto product baseline. Users switching between JS and C# versions should see equivalent:

1. Information hierarchy
2. Visual semantics (color and emphasis)
3. Interaction affordances (hints, status surfaces)
4. Terminal rhythm (spacing, separators, line density)

Without parity, users perceive regressions even when behavior is technically correct.

### 1.3 Key Reference Sources

JavaScript baseline composition and theme:

- ../../javascript/packages/coding-agent/src/modes/interactive/interactive-mode.ts
- ../../javascript/packages/coding-agent/src/modes/interactive/theme/theme.ts
- ../../javascript/packages/coding-agent/src/modes/interactive/theme/dark.json

PiSharp implementation to align:

- ../../src/PiSharp.Tui/Interactive/TuiHost.cs
- ../../src/PiSharp.Tui/Interactive/Theme/TuiTheme.cs
- ../../src/PiSharp.Tui/Interactive/Components/\*.cs
- ../../src/PiSharp.Tui/Interactive/Rendering/\*.cs

## 2. Problem Statement

Align PiSharp base interactive TUI to be as visually and structurally identical as possible to the JavaScript baseline, while preserving:

1. Existing PiSharp architecture boundaries
2. Terminal.Gui constraints
3. Base-mode scope (no extension-specific visual requirements beyond neutral compatibility)

The parity target is not abstract "nicer UI". It is explicit, testable parity to the JS baseline.

## 3. Goals And Non-Goals

### 3.1 Goals

1. Create a parity contract that defines the target UI behavior and visuals.
2. Introduce a semantic theme token layer in PiSharp matching JS token intent.
3. Align layout composition order and spacing with JS interactive mode.
4. Align message renderers and tool execution states with JS visual semantics.
5. Align footer content architecture and formatting with JS footer behavior.
6. Add deterministic parity tests and approval-style snapshots.
7. Keep base mode maintainable and explicit about any unavoidable deltas.

### 3.2 Non-Goals

1. Full reimplementation of JS TUI engine internals in C#.
2. Exact ANSI sequence byte-for-byte parity.
3. Feature parity for every optional JS easter egg or extension-provided component.
4. Web UI parity (this epic is TUI-only).
5. Major redesign of agent harness/session core behavior.

## 4. Constraints And Assumptions

### 4.1 Technical Constraints

1. Terminal.Gui color handling differs from direct ANSI in JS.
2. Font rendering and Unicode glyph width are terminal-dependent.
3. Terminal size, OS shell, and color capability influence final appearance.

### 4.2 Product Constraints

1. "Base version" means PiSharp without optional extension custom surfaces.
2. Existing key interactions (submit, interrupt, history, toggles) must remain stable.
3. No regressions in accessibility of status, errors, and tool execution feedback.

### 4.3 Parity Scope Assumption

Parity means functional visual equivalence from a user perspective:

1. Same information shown in equivalent places
2. Same state-to-style mapping
3. Same default spacing and separation rhythm
4. Same default onboarding/header/footer shape

## 5. Current Gap Matrix

## 5.1 Theme System Gap

JS:

- Rich semantic token model with JSON themes and runtime mapping.
- Example files:
  - ../../javascript/packages/coding-agent/src/modes/interactive/theme/theme.ts
  - ../../javascript/packages/coding-agent/src/modes/interactive/theme/dark.json

PiSharp:

- Static color schemes and row attributes.
- File:
  - ../../src/PiSharp.Tui/Interactive/Theme/TuiTheme.cs

Gap:

- Missing token granularity for user message bg/text, tool state backgrounds, markdown variants, diff colors, thinking levels, and muted/border variants.

## 5.2 Layout Composition Gap

JS composition order includes header, chat, pending/status containers, widget slots, editor, and footer with tuned spacing.

PiSharp composition is simpler and not fully equivalent.

Files:

- JS: ../../javascript/packages/coding-agent/src/modes/interactive/interactive-mode.ts
- C#: ../../src/PiSharp.Tui/Interactive/TuiHost.cs

## 5.3 Header/Onboarding Gap

JS has compact vs expanded onboarding hints and explicit keybinding hint grammar.

PiSharp header is fixed 3-line text.

Files:

- JS: ../../javascript/packages/coding-agent/src/modes/interactive/interactive-mode.ts
- JS helper: ../../javascript/packages/coding-agent/src/modes/interactive/components/keybinding-hints.ts
- C#: ../../src/PiSharp.Tui/Interactive/Components/HeaderView.cs
- C#: ../../src/PiSharp.Tui/Interactive/TuiKeybindings.cs

## 5.4 Message Surface Gap

JS uses component-specific rendering for user, assistant, tool, and custom messages with theme-aware backgrounds.

PiSharp uses row kind attributes and simpler box/wrap behavior.

Files:

- JS: ../../javascript/packages/coding-agent/src/modes/interactive/components/user-message.ts
- JS: ../../javascript/packages/coding-agent/src/modes/interactive/components/assistant-message.ts
- JS: ../../javascript/packages/coding-agent/src/modes/interactive/components/tool-execution.ts
- JS: ../../javascript/packages/coding-agent/src/modes/interactive/components/custom-message.ts
- C#: ../../src/PiSharp.Tui/Interactive/Components/MessageRenderers.cs
- C#: ../../src/PiSharp.Tui/Interactive/Components/ToolExecutionView.cs
- C#: ../../src/PiSharp.Tui/Interactive/Rendering/TuiMarkdownRenderer.cs

## 5.5 Footer Gap

JS footer emphasizes concise token abbreviations, context percentage coloring, right-aligned model/provider details, and optional extension statuses.

PiSharp footer format and data model differ.

Files:

- JS: ../../javascript/packages/coding-agent/src/modes/interactive/components/footer.ts
- JS: ../../javascript/packages/coding-agent/src/core/footer-data-provider.ts
- C#: ../../src/PiSharp.Tui/Interactive/Components/FooterView.cs
- C#: ../../src/PiSharp.Tui/Interactive/FooterDataProvider.cs

## 6. Target State

At epic completion, PiSharp base interactive mode should satisfy:

1. Visual parity contract coverage for all major surfaces.
2. Default dark theme semantics matching JS token intent.
3. Equivalent layout rhythm and composition order.
4. Equivalent message and tool visual states.
5. Equivalent footer information architecture and formatting patterns.
6. Snapshot-based regression guards with documented acceptable deltas.

## 7. PR-Sized Task Breakdown

## 7.1 Task 1: Create Visual Parity Contract Document

Scope:

1. Add a detailed parity contract doc capturing "source of truth" by surface.
2. Define mandatory parity checks and allowed deltas.

Suggested files:

- ../../docs/specs/ (new document, naming consistent with repo convention)

Definition of Done:

1. Contract includes header, chat, tool states, prompt/editor, footer, borders, spacing, and token semantics.
2. Every contract item references both JS source and PiSharp target file(s).
3. Allowed deltas are explicit and justified.

Acceptance Criteria:

1. Reviewer can use the contract as a checklist without opening this epic.
2. No ambiguous terms like "similar" without measurable criteria.

Tests:

1. N/A (documentation task).

## 7.2 Task 2: Introduce Semantic Theme Token Layer In PiSharp

Scope:

1. Replace hard-coded row/color mapping with tokenized semantic theme API.
2. Map PiSharp default theme to JS dark token intent.
3. Keep compatibility wrappers for existing callers during migration.

Primary files:

- ../../src/PiSharp.Tui/Interactive/Theme/TuiTheme.cs
- ../../src/PiSharp.Tui/Interactive/Components/\*.cs (call-site updates)

Definition of Done:

1. PiSharp exposes semantic tokens covering at least: accent, border variants, muted/dim/text, user message bg/text, tool pending/success/error backgrounds, markdown tokens, diff tokens, thinking tokens.
2. Existing UI compiles and renders via token APIs.
3. No dead/duplicate color constants remain in old shape.

Acceptance Criteria:

1. Theme token names and intent are traceable to JS token set.
2. Changing one token updates all dependent surfaces consistently.

Tests:

1. Unit tests for token-to-attribute mapping behavior.
2. Golden tests for default token palette export/serialization (if applicable).

## 7.3 Task 3: Align TuiHost Composition And Vertical Rhythm

Scope:

1. Rework container stacking and reserved heights to match JS structure as closely as feasible.
2. Normalize separators and spacing rules around header/chat/prompt/footer.

Primary files:

- ../../src/PiSharp.Tui/Interactive/TuiHost.cs
- ../../src/PiSharp.Tui/Interactive/Components/HeaderView.cs
- ../../src/PiSharp.Tui/Interactive/Components/ChatView.cs
- ../../src/PiSharp.Tui/Interactive/Components/PromptEditor.cs
- ../../src/PiSharp.Tui/Interactive/Components/FooterView.cs

Definition of Done:

1. Composition order mirrors JS conceptual model.
2. Spacing between major surfaces matches parity contract at standard terminal sizes (80x24, 120x40).
3. Scroll and focus behavior remains stable.

Acceptance Criteria:

1. Side-by-side render captures show equivalent section boundaries and line density.
2. No regression in prompt usability or chat scrolling shortcuts.

Tests:

1. Layout snapshot tests for multiple terminal dimensions.
2. Interaction tests for focus retention and scrolling.

## 7.4 Task 4: Header And Onboarding Parity

Scope:

1. Move from static header text to compact/expanded onboarding model.
2. Align keybinding hint formatting and terminology with JS baseline.

Primary files:

- ../../src/PiSharp.Tui/Interactive/Components/HeaderView.cs
- ../../src/PiSharp.Tui/Interactive/TuiKeybindings.cs
- ../../src/PiSharp.Tui/Interactive/TuiHost.cs

Definition of Done:

1. Compact default header mode implemented.
2. Expanded mode and toggle path implemented.
3. Key hints generated from keybinding definitions (not hand-copied strings).

Acceptance Criteria:

1. Header content hierarchy mirrors JS baseline intent.
2. Missing/unbound keybindings degrade gracefully.

Tests:

1. Unit tests for hint formatting and fallback behavior.
2. Snapshot tests for compact and expanded variants.

## 7.5 Task 5: Message Rendering Parity (User/Assistant/Thinking/Custom)

Scope:

1. Align message block styling, padding, and spacing.
2. Align assistant/thinking rendering semantics and hidden-thinking label behavior.
3. Improve markdown token mapping to JS-like semantics.

Primary files:

- ../../src/PiSharp.Tui/Interactive/Components/MessageRenderers.cs
- ../../src/PiSharp.Tui/Interactive/Rendering/TuiMarkdownRenderer.cs
- ../../src/PiSharp.Tui/Interactive/Components/ChatView.cs

Definition of Done:

1. User messages render with equivalent visual affordance to JS baseline.
2. Assistant text/thinking blocks follow parity contract ordering and spacing rules.
3. Custom/system/error message styling follows token semantics.

Acceptance Criteria:

1. Visual differences to JS are either negligible or documented as accepted deltas.
2. No markdown readability regressions (headings, lists, code blocks, links).

Tests:

1. Snapshot tests covering multi-block assistant messages (text + thinking + errors).
2. Markdown renderer tests for headings/lists/code/link transformations.

## 7.6 Task 6: Tool Execution Surface Parity

Scope:

1. Align pending/success/error visual states and title/output emphasis.
2. Align collapsed/expanded tool output behavior and prefixes.

Primary files:

- ../../src/PiSharp.Tui/Interactive/Components/ToolExecutionView.cs
- ../../src/PiSharp.Tui/Interactive/Components/MessageRenderers.cs

Definition of Done:

1. Tool state styling maps to tokenized pending/success/error backgrounds.
2. Expanded/collapsed rendering follows parity contract.
3. Tool output formatting supports readable wrapped output and state transitions.

Acceptance Criteria:

1. Tool lifecycle (start, stream, complete, fail) remains understandable at a glance.
2. Keyboard toggles still control expansion correctly.

Tests:

1. Unit tests for state-to-row rendering.
2. Snapshot tests for representative tool lifecycle transcripts.

## 7.7 Task 7: Footer Data And Rendering Parity

Scope:

1. Align footer line structure, token abbreviations, and context usage coloring.
2. Add right-side model/provider formatting logic compatible with width constraints.
3. Improve git branch/provider/session info behavior to match JS intent.

Primary files:

- ../../src/PiSharp.Tui/Interactive/Components/FooterView.cs
- ../../src/PiSharp.Tui/Interactive/FooterDataProvider.cs

Definition of Done:

1. Footer exposes equivalent major fields: cwd/session, token stats, cost, context usage, model/thinking/provider details.
2. Width-aware truncation/alignment rules implemented and tested.
3. Extension status line behavior remains compatible with base mode.

Acceptance Criteria:

1. Footer remains legible at narrow widths.
2. Context usage warning/error thresholds follow parity contract.

Tests:

1. Deterministic footer formatting tests for multiple width and usage scenarios.
2. Git branch provider tests (branch present, detached, unavailable).

## 7.8 Task 8: Parity Snapshot Harness And Regression Suite

Scope:

1. Add deterministic transcript fixtures and expected render outputs.
2. Build approval-style snapshot tests for key surfaces and scenarios.
3. Document test workflow and update process.

Primary files:

- ../../tests/PiSharp.Tui.Tests/ (new parity test files)
- ../../docs/ (testing documentation update)

Definition of Done:

1. Test suite covers baseline scenarios: startup, user/assistant exchange, thinking visible/hidden, tool lifecycle, markdown-rich responses, narrow terminal fallback, footer stress cases.
2. CI executes snapshot tests deterministically.
3. Snapshot update process is documented.

Acceptance Criteria:

1. Any intentional visual change requires explicit snapshot update and reviewer sign-off.
2. Tests catch accidental spacing/color/state regressions.

Tests:

1. New parity snapshot suite in PiSharp.Tui.Tests.
2. Existing TUI tests remain green.

## 8. Cross-Cutting Definition Of Done (Epic Level)

The epic is complete only when all of the following are true:

1. All planned tasks are merged with passing CI.
2. Parity contract checklist is marked complete or explicitly deferred per item.
3. Remaining deltas are documented with rationale and severity.
4. PiSharp base TUI side-by-side review against JS baseline is signed off.
5. No critical regressions in input handling, scrolling, or status visibility.

## 9. Acceptance Criteria (Epic Level)

1. A reviewer unfamiliar with the implementation can compare PiSharp and JS default interactive mode and recognize equivalent information architecture and state styling.
2. Color/state semantics are consistent across message types and tool states.
3. Footer conveys equivalent operational context and remains readable under constrained widths.
4. Snapshot suite provides durable protection against parity regressions.
5. Base mode parity does not require extension features.

## 10. Test Strategy

### 10.1 Unit Tests

1. Theme token mapping behavior and fallback rules.
2. Footer formatting logic across width and data combinations.
3. Message and tool renderers for state transitions and wrapping.
4. Header hint formatting and compact/expanded mode selection.

### 10.2 Snapshot/Approval Tests

1. Canonical transcript fixture renders (80x24, 120x40).
2. Markdown-rich output fixtures.
3. Tool lifecycle fixtures (pending, partial, success, failure).
4. Hidden-thinking and shown-thinking fixtures.

### 10.3 Manual Validation Matrix

1. Windows Terminal, default dark profile.
2. At least one non-Windows terminal profile where available.
3. Narrow and wide terminal sizes.
4. Long session transcript with scrolling and repeated tool calls.

### 10.4 Regression Guardrails

1. Snapshot tests are required in CI for TUI-related PRs.
2. Any visual behavior change requires parity contract update or approved delta note.

## 11. Risks And Mitigations

1. Risk: Terminal.Gui cannot fully replicate ANSI-level JS styling.
   Mitigation: Declare allowed deltas early in parity contract and optimize for semantic parity.

2. Risk: Scope creep into feature parity beyond visuals.
   Mitigation: Keep epic scope constrained to layout/theme/render surfaces.

3. Risk: Snapshot brittleness.
   Mitigation: Use stable fixtures and normalize non-deterministic fields.

4. Risk: Footer complexity causes readability regressions.
   Mitigation: Add width-aware tests and explicit truncation policies.

## 12. Dependencies

1. EPIC-05 CLI/modes conventions for interactive mode behavior.
2. EPIC-06 extension/TS bridge compatibility considerations for base mode interaction points.
3. Existing PiSharp.Tui test harness enhancements (if needed) to support deterministic snapshot assertions.

## 13. Out Of Scope / Follow-Ups

1. Theme switching UX parity beyond default baseline theme.
2. Optional/easter-egg components in JS interactive mode.
3. Advanced extension-custom header/footer composition parity.
4. Web UI look-and-feel alignment.

## 14. Implementation Notes For Reviewers

1. Prioritize semantic parity over pixel-perfect assumptions.
2. Keep changes incremental and PR-reviewable (one surface area at a time).
3. Every PR should include:
   - before/after render captures or snapshots
   - checklist mapping to parity contract items
   - explicit statement of any new deltas introduced

## 15. Suggested Milestone Plan

1. Milestone A: Contract + theme token foundation
2. Milestone B: layout and header parity
3. Milestone C: message + tool parity
4. Milestone D: footer parity
5. Milestone E: snapshot hardening and final parity review

## 16. Implementation Checklist (Per-PR Template)

Use this checklist in every PR linked to this epic. Copy into the PR description and fill it out.

### 16.1 Scope And Traceability

- [ ] PR title follows: `EPIC-09 / Task X / <surface>`
- [ ] PR links to EPIC-09 and specific task number(s) from Section 7
- [ ] PR states whether it is `feature parity`, `test-only`, or `refactor-only`
- [ ] PR includes a short "out of scope" list
- [ ] PR lists the exact files changed

### 16.2 Parity Contract Mapping

- [ ] Every visual change maps to an item in the parity contract doc
- [ ] Any behavior not covered by the contract is explicitly called out
- [ ] Any deviation from JS baseline is listed as a delta with rationale
- [ ] Delta severity is labeled: `minor`, `moderate`, or `major`

### 16.3 UI Surface Checklist

Mark only the surfaces touched by the PR.

- [ ] Theme tokens / color semantics
- [ ] Layout composition / spacing rhythm
- [ ] Header / onboarding / key hints
- [ ] User message rendering
- [ ] Assistant + thinking rendering
- [ ] Tool execution rendering
- [ ] Footer data + footer rendering
- [ ] Markdown styling
- [ ] Borders / separators
- [ ] Prompt/editor integration

### 16.4 Test Coverage Checklist

- [ ] Added or updated unit tests for modified logic
- [ ] Added or updated snapshot/approval tests where rendering changed
- [ ] Verified snapshots for at least `80x24` and `120x40`
- [ ] Verified one narrow-width stress case
- [ ] Existing related tests remain green

### 16.5 Manual Verification Checklist

- [ ] Captured before/after screenshots or render captures
- [ ] Verified readability in Windows Terminal dark profile
- [ ] Verified scrolling and prompt focus behavior
- [ ] Verified tool state transitions (pending/success/failure) if relevant
- [ ] Verified no obvious regressions in keyboard interaction

### 16.6 Quality Gates

- [ ] No unrelated refactors mixed into PR
- [ ] No dead code or unused constants introduced
- [ ] Naming aligns with existing conventions
- [ ] Reviewer can validate changes from PR description without source-diving first

### 16.7 Definition Of Done Confirmation (Per PR)

- [ ] Task-specific DoD items from Section 7 are complete
- [ ] Task-specific acceptance criteria from Section 7 are satisfied
- [ ] Test expectations from Section 7 are satisfied
- [ ] Any remaining follow-up work is captured as a concrete TODO issue/task

### 16.8 PR Summary Template

Use this exact structure in PR descriptions:

```md
## EPIC-09 Task Mapping

- Task: 7.X
- Type: feature parity | test-only | refactor-only

## What Changed

- ...

## Parity Contract Coverage

- Covered items: ...
- New deltas introduced: none | ...

## Tests

- Unit: ...
- Snapshot: ...
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
- [ ] All documented deltas are reviewed and accepted
- [ ] Final side-by-side parity review artifacts are attached
- [ ] Epic status is updated from `proposed` to final state

## 17. Implementation Summary (2026-05-27)

### 17.1 Completed In This Iteration

1. Added a parity contract document:
  - ../../docs/specs/TUI-visual-parity-contract.md
2. Introduced semantic theme token APIs and row-kind mapping updates:
  - ../../src/PiSharp.Tui/Interactive/Theme/TuiTheme.cs
  - ../../src/PiSharp.Tui/Interactive/Rendering/TuiChatRow.cs
3. Implemented compact + expanded header rendering with generated keybinding hints:
  - ../../src/PiSharp.Tui/Interactive/Components/HeaderView.cs
  - ../../src/PiSharp.Tui/Interactive/TuiKeybindings.cs
  - ../../src/PiSharp.Tui/Interactive/Components/PromptEditor.cs
  - ../../src/PiSharp.Tui/Interactive/TuiHost.cs
4. Improved message and tool rendering semantics for parity readability:
  - ../../src/PiSharp.Tui/Interactive/Components/MessageRenderers.cs
  - ../../src/PiSharp.Tui/Interactive/Components/ToolExecutionView.cs
5. Reworked footer information architecture and width-aware alignment:
  - ../../src/PiSharp.Tui/Interactive/Components/FooterView.cs
  - ../../src/PiSharp.Tui/Interactive/FooterDataProvider.cs
6. Added deterministic snapshot-style parity tests and fixtures:
  - ../../tests/PiSharp.Tui.Tests/TuiParitySnapshotTests.cs
  - ../../tests/PiSharp.Tui.Tests/Snapshots/*.snap

### 17.2 Challenges And Decisions

1. Terminal.Gui color behavior is not 1:1 with JS ANSI rendering.
  - Decision: implement semantic token mapping and preserve allowed terminal-color deltas in the parity contract.
2. Footer parity requires width-aware layout with constrained model metadata.
  - Decision: prioritize stable left stats + right model/thinking alignment and deterministic truncation.
3. Full composition parity (all JS containers/widgets) is broader than this single pass.
  - Decision: complete high-impact visual surfaces first (header/messages/tools/footer/theme + snapshot guardrails), then iterate on host layout rhythm in follow-up PRs.

### 17.3 Remaining Follow-Ups

1. Add 80x24 and 120x40 full transcript host-level layout snapshots.
2. Add manual side-by-side parity artifacts for final epic sign-off.
3. Expand markdown token-aware styling once multi-span color rendering is fully wired through Terminal.Gui draw paths.
