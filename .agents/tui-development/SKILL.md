---
name: tui-development
description: >
  Use when changing the interactive Terminal.Gui shell: chat view, prompt
  editor, keybindings/shortcuts, dialogs, diff view, selector, session tree,
  rendering, or the remote TUI backend. Covers the TUI's relationship to
  client session state, the keybinding regression guard in TuiRenderingTests,
  and the shortcut catalog pattern.
type: application
scope:
  - src/PiSharp.Tui/**
  - tests/PiSharp.Tui.Tests/**
  - docs/pisharp-tui-shortcut-development.md
  - docs/pisharp-tui-tracing.md
  - docs/terminal-gui-architecture-reference.md
related_skills:
  - daemon-protocol
  - local-development
last_verified:
  commit: "646522ccc6edc48acc39e4545cd120af9f1dafba"
  date: "2026-08-14"
confidence: high
---

# TUI Shell and Keybindings

## When to use this skill

Use this skill when:

- adding or changing a TUI shortcut/keybinding;
- changing the prompt editor behavior;
- altering chat view rendering or any Interactive/ subtree view;
- changing the remote TUI backend;
- debugging TUI rendering or performance;
- preserving PiSharp-owned keybindings.

Typical tasks include:

- adding a shortcut to the catalog;
- changing `TuiRenderState` derivation from client state;
- fixing a rendering regression caught by `TuiRenderingTests.cs`;
- wiring a new daemon state field into the TUI.

Do not use this skill for:

- daemon wire protocol — use [daemon-protocol](../daemon-protocol/SKILL.md);
- general build/test — use [local-development](../local-development/SKILL.md).

## Responsibilities and boundaries

This area owns:

- the interactive shell views (chat, prompt editor, dialogs, diff, selector,
  session tree);
- keybinding/shortcut handling;
- rendering state derivation;
- remote TUI backend connectivity to the daemon.

This area does not own:

- the daemon protocol itself (daemon-protocol);
- the agent loop (agent-harness);
- the model provider layer (model-providers).

## Architecture

The TUI is a Terminal.Gui 2.0 application (`TuiHost` + `Interactive/`
subtree). It runs either in-process (`--local` / tests) or against the daemon
via `RemoteTuiBackend`. Rendering is driven by `TuiRenderState`, derived from
`ClientSessionState` each frame — the TUI does not own session state, it
projects it.

PiSharp-owned keybindings are a regression surface: `TuiRenderingTests.cs`
validates rendering/keybinding behavior and must keep passing.

### Important components

| Component | Location | Responsibility |
|---|---|---|
| TUI host | `src/PiSharp.Tui` (TuiHost) | Terminal.Gui app shell |
| Interactive views | `src/PiSharp.Tui/Interactive/` | Chat view, prompt editor, dialogs, diff, selector, session tree |
| Render state | `src/PiSharp.Tui` (`TuiRenderState`) | Per-frame projection of client state |
| Remote backend | `src/PiSharp.Tui` (`RemoteTuiBackend`) | Talks to the daemon over the wire protocol |
| Rendering tests | `tests/PiSharp.Tui.Tests/TuiRenderingTests.cs` | Keybinding/rendering regression guard |
| Shortcut docs | `docs/pisharp-tui-shortcut-development.md` | Shortcut catalog pattern |

### Main flow

1. Client connects to daemon (or runs in-process with `--local`).
2. Client session state updates arrive; `TuiRenderState` is derived per frame.
3. Terminal.Gui renders the active view; keybindings dispatch to actions.
4. User actions send commands back through the client (see
   [daemon-protocol](../daemon-protocol/SKILL.md)).

## Project terminology

| Term | Meaning in this repository |
|---|---|
| TuiHost | The Terminal.Gui application shell |
| TuiRenderState | Per-frame projection of `ClientSessionState` |
| RemoteTuiBackend | TUI's connection to the daemon |
| Prompt editor | The multi-line input editor in the chat view |
| PiSharp-owned keybindings | Shortcuts whose behavior is guarded by rendering tests |

## Important entry points
- [`skills/SKILL.md`](../../SKILL.md): project router — routing index for all PiSharp project skills.


- [`src/PiSharp.Tui`](../../../src/PiSharp.Tui): the shell.
- [`tests/PiSharp.Tui.Tests/TuiRenderingTests.cs`](../../../tests/PiSharp.Tui.Tests/TuiRenderingTests.cs):
  the keybinding/rendering guard.
- [`docs/pisharp-tui-shortcut-development.md`](../../../docs/pisharp-tui-shortcut-development.md):
  shortcut catalog pattern.
- [`docs/terminal-gui-architecture-reference.md`](../../../docs/terminal-gui-architecture-reference.md):
  Terminal.Gui reference (local toolkit docs also exist on the dev machine).

## Dependencies and consumers

### Depends on

- Terminal.Gui 2.0 (external UI library).
- `src/PiSharp.Client` (session state), `src/PiSharp.Server` via wire protocol
  (remote mode).

### Consumed by

- End users (interactive mode); tests drive it headlessly.

### External systems

- Terminal.Gui 2.0 package.

## Invariants

The following must remain true:

1. `TuiRenderState` is derived from `ClientSessionState` — the TUI never owns
   authoritative session state.
2. PiSharp-owned keybindings behave as validated in `TuiRenderingTests.cs`.
3. Rendering tests stay deterministic (headless-friendly).
4. In remote mode the TUI only talks to the daemon through the wire protocol.

## Common change workflows

### Add a TUI shortcut

1. Follow the shortcut catalog pattern in
   `docs/pisharp-tui-shortcut-development.md`.
2. Add the keybinding handler in the relevant `Interactive/` view.
3. Add a rendering test to `TuiRenderingTests.cs` covering the new behavior
   (and that existing PiSharp-owned bindings still behave).
4. Update `docs/pisharp-tui-shortcut-development.md` shortcut catalog.

Files commonly changed together:

- `src/PiSharp.Tui/Interactive/**`
- `tests/PiSharp.Tui.Tests/TuiRenderingTests.cs`
- `docs/pisharp-tui-shortcut-development.md`

Validation:

```bash
dotnet test tests/PiSharp.Tui.Tests/PiSharp.Tui.Tests.csproj --filter Keybinding
```

### Change prompt editor behavior

1. Locate the prompt editor view in `src/PiSharp.Tui/Interactive/`.
2. Change behavior; keep PiSharp-owned keybindings intact (guard tests exist).
3. Run the TUI test project.

Files commonly changed together:

- `src/PiSharp.Tui/Interactive/**`
- `tests/PiSharp.Tui.Tests/**`

Validation:

```bash
dotnet test tests/PiSharp.Tui.Tests/PiSharp.Tui.Tests.csproj
```

## Testing and validation

Run for all changes in this area:

```bash
dotnet test tests/PiSharp.Tui.Tests/PiSharp.Tui.Tests.csproj
```

Run conditionally:

```bash
dotnet build PiSharp.sln
dotnet test PiSharp.sln
```

## Operational considerations

- Rendering tests are the regression guard for keybindings; never bypass them
  to ship a shortcut change.
- Terminal.Gui is an external dependency with its own quirks; the three
  reference docs in `docs/` describe its architecture/examples/input handling.

## Common mistakes

- Do not break PiSharp-owned keybindings — `TuiRenderingTests.cs` validates
  them; run it before finishing.
- Do not let the TUI hold authoritative session state — derive from
  `ClientSessionState`.
- Do not call the daemon from the TUI outside the wire protocol in remote mode.
- Do not make rendering tests interactive or timing-dependent.

## Legacy and deprecated patterns

- The original JS Pi TUI behavior is reference-only; PiSharp's TUI is a
  Terminal.Gui reimplementation.

## Existing authoritative documentation

- [`docs/pisharp-tui-shortcut-development.md`](../../../docs/pisharp-tui-shortcut-development.md)

  * Covers the shortcut catalog pattern.
  * Treat as authoritative for adding shortcuts.

- [`docs/terminal-gui-architecture-reference.md`](../../../docs/terminal-gui-architecture-reference.md)
  and sibling reference docs

  * Covers Terminal.Gui architecture/examples/input.
  * Treat as authoritative for the external library.

## Known ambiguity and technical debt

- Terminal.Gui 2.0 rendering is layout-sensitive; changes can have
  cross-view effects caught only by full TUI tests.
- Remote vs in-process mode differences are a common source of
  "works locally, breaks remote" bugs.

## Evidence and verification

This skill was verified against commit `646522ccc6edc48acc39e4545cd120af9f1dafba`.

Primary evidence:

- [`tests/PiSharp.Tui.Tests/TuiRenderingTests.cs`](../../../tests/PiSharp.Tui.Tests/TuiRenderingTests.cs)
- [`docs/pisharp-tui-shortcut-development.md`](../../../docs/pisharp-tui-shortcut-development.md)
- [`src/PiSharp.Tui`](../../../src/PiSharp.Tui)
- [`AGENTS.md`](../../../AGENTS.md) (TUI keybinding pitfall note)
