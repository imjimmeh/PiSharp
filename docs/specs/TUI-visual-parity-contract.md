# TUI Visual Parity Contract (EPIC-09)

## Scope

This contract defines baseline parity requirements between:

- JavaScript interactive mode in javascript/packages/coding-agent/src/modes/interactive
- C# interactive mode in src/PiSharp.Tui/Interactive

Parity target is semantic equivalence (information architecture, state mapping, spacing rhythm), not byte-for-byte ANSI output.

## Mandatory Surfaces

1. Header
- Compact mode is default and shows product identity, status, and key interaction hints.
- Expanded mode includes model and thinking details.
- Key hint labels are generated from C# keybinding definitions.

2. Transcript
- User messages use a distinct background-style container.
- Assistant text and thinking are visually distinct.
- Thinking-hidden state renders a deterministic placeholder label.
- System and error messages use explicit state markers.

3. Tool execution
- Tool states map to pending/success/error semantics.
- Collapsed mode shows one summary line.
- Expanded mode includes wrapped output body.

4. Footer
- First line includes cwd/session context and status.
- Stats line uses compact token abbreviations (up/down/cache/cost/context).
- Model/thinking information is right-aligned when width allows.
- Context utilization has warning/error semantic thresholds.
- Extension statuses render in deterministic key order.

5. Theme tokens
- TUI uses semantic tokens, not ad-hoc per-surface colors.
- At minimum, token set covers accent, border variants, muted/dim/text, user/tool/custom backgrounds, markdown tokens, diff tokens, and thinking levels.

## Allowed Deltas

1. Terminal.Gui color precision
- 24-bit parity can degrade to nearest available terminal color.

2. Unicode width variance
- Character width differences across terminals may shift alignment by one column.

3. Footer truncation behavior
- Exact clipping points may differ slightly as long as major fields remain visible and readable.

## Verification Matrix

1. Automated
- Unit tests for theme token mapping and footer formatting.
- Snapshot tests for assistant/thinking/tool rendering and narrow footer behavior.

2. Manual
- Compare C# and JS at 80x24 and 120x40.
- Verify scrolling, prompt focus, and tool lifecycle readability.

## Snapshot Workflow

1. Run parity tests in tests/PiSharp.Tui.Tests.
2. If behavior changes intentionally, update the relevant .snap file in tests/PiSharp.Tui.Tests/Snapshots.
3. Re-run tests and include the snapshot delta in PR review notes.
4. Any snapshot update must mention whether the change is parity-improving or an accepted delta.

## Traceability

- Theme: src/PiSharp.Tui/Interactive/Theme/TuiTheme.cs
- Header: src/PiSharp.Tui/Interactive/Components/HeaderView.cs
- Transcript/messages: src/PiSharp.Tui/Interactive/Components/MessageRenderers.cs
- Tools: src/PiSharp.Tui/Interactive/Components/ToolExecutionView.cs
- Footer data/rendering: src/PiSharp.Tui/Interactive/FooterDataProvider.cs and src/PiSharp.Tui/Interactive/Components/FooterView.cs
- Snapshot suite: tests/PiSharp.Tui.Tests/TuiParitySnapshotTests.cs and tests/PiSharp.Tui.Tests/Snapshots/*.snap
