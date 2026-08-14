---
name: repository-overview
description: >
  Use when orienting on the PiSharp repository, understanding the project map
  and architecture layers, deciding which project owns a concern, choosing
  which documentation is authoritative, or checking whether a change belongs
  in core versus an extension. Also covers the javascript/ reference-only
  guardrail and the daemon/client split.
type: orientation
scope:
  - src/**
  - tests/**
  - docs/**
  - AGENTS.md
  - PiSharp.sln
related_skills:
  - local-development
  - extension-platform
  - daemon-protocol
  - plugin-portfolio
last_verified:
  commit: "646522ccc6edc48acc39e4545cd120af9f1dafba"
  date: "2026-08-14"
confidence: high
---

# PiSharp Repository Overview

## When to use this skill

Use this skill when:

- starting work in this repository for the first time;
- asking "where does this concern live?" or "which project owns this?";
- deciding whether a change belongs in core/runtime/CLI/TUI versus an extension;
- checking which documentation describes the current code;
- reading `AGENTS.md`, `docs/pisharp-developer-guide.md`, or `docs/specs/SDD-pi-csharp-port.md`.

Typical tasks include:

- orienting on the repository layout before a change;
- locating the project that owns a feature;
- routing to the correct specialized skill (see the [project router](../../SKILL.md)).

Do not use this skill for:

- build/test command specifics — use [local-development](../local-development/SKILL.md);
- extension authoring — use [extension-platform](../extension-platform/SKILL.md);
- deep dives into a shipped plugin — use [plugin-portfolio](../plugin-portfolio/SKILL.md);
- the daemon wire protocol — use [daemon-protocol](../daemon-protocol/SKILL.md).

## Responsibilities and boundaries

This area owns:

- the overall project map (52 C# `src` projects, 43 test projects, 1 webapp);
- architecture layer discipline and dependency boundaries;
- the extension-first policy;
- the `javascript/` reference-only guardrail;
- documentation authority (which docs describe current code).

This area does not own:

- any single concern's implementation details (they belong to the concern's skill);
- build/test commands (local-development);
- the parity contract (tsbridge-parity).

## Architecture

PiSharp is a C# port of the original JavaScript "Pi" coding agent, organized as:

- **Core contract layers**: `src/PiSharp.Abstractions` and `src/PiSharp.Agent.Core`
  are dependency-light contract layers.
- **Runtime**: `src/PiSharp.Runtime` holds runtime wiring/composition
  (`PiRuntimeBootstrap.CreateRuntimeAsync`).
- **Daemon**: `src/PiSharp.Server` hosts the daemon (Kestrel, `/health` + `/ws`,
  API-key auth); `src/PiSharp.Client` is the event-sourced WebSocket client;
  `src/PiSharp.Cli` is the executable (client mode, `daemon foreground`,
  `acp`, non-interactive, etc.); `src/PiSharp.Sdk` is the programmatic SDK.
- **Extension bridge**: `src/PiSharp.TsBridge` (TypeScript bridge + Node sidecar)
  and `src/PiSharp.PluginHost` (native plugin loading in a collectible ALC).
- **Extensions**: `src/PiSharp.Extensions` owns the shared extension API
  (`IExtensionApi`, `ExtensionManager`); shipped plugins live in individual
  `src/PiSharp.*` projects (see [plugin-portfolio](../plugin-portfolio/SKILL.md)).
- **Webapp**: `src/pisharp-session-webapp` is a Vite/TypeScript app, not part of
  the .NET solution.

### Important components

| Component | Location | Responsibility |
|---|---|---|
| Solution | `PiSharp.sln` | 52 src + 43 test C# projects (4 solution folders); webapp and `extensions/` are NOT members |
| Abstractions | `src/PiSharp.Abstractions` | Dependency-light contracts |
| Agent core | `src/PiSharp.Agent.Core` | Dependency-light contracts + agent abstractions |
| Runtime wiring | `src/PiSharp.Runtime` | Composition root; `PiRuntimeBootstrap` |
| Daemon | `src/PiSharp.Server` | Kestrel host, WebSocket hub, warm session runtimes |
| Client | `src/PiSharp.Client` | Event-sourced WS client + reducer |
| CLI | `src/PiSharp.Cli` | Executable: client, daemon foreground, acp, non-interactive |
| TsBridge | `src/PiSharp.TsBridge` | TypeScript extension bridge + Node sidecar |
| PluginHost | `src/PiSharp.PluginHost` | Native plugin loading (collectible ALC) |
| Extension API | `src/PiSharp.Extensions` | `IExtensionApi`, `ExtensionManager` |
| Webapp | `src/pisharp-session-webapp` | Vite/TS frontend (not in solution) |

### Main flow

1. `PiSharp.Cli` starts as client (default), `daemon foreground`, `acp`, or non-interactive.
2. Client talks to daemon over WebSocket using the event-sourced protocol
   (see [daemon-protocol](../daemon-protocol/SKILL.md)); `--local` runs in-process.
3. Daemon hosts warm `SessionRuntime`s; `PiRuntimeBootstrap` composes the
   harness, session repo, extension manager, and plugin host.
4. Extensions (native + TypeScript) register tools/commands/hooks through one
   registry surface (see [extension-platform](../extension-platform/SKILL.md)).

## Project terminology

| Term | Meaning in this repository |
|---|---|
| Harness | `AgentHarness<TMetadata>`, the per-session agent loop coordinator |
| Runtime | `SessionRuntime`, a warm session + harness factory instance |
| Daemon | Long-lived `PiSharp.Server` process hosting warm session runtimes |
| Client | `PiSharp.Cli`/`PiSharp.Client` event-sourced WS client |
| Extension | Native `.dll` or TypeScript add-on registering through `IExtensionApi` |
| Plugin | A shipped native extension in the P07-P31 portfolio |
| TsBridge | The Node sidecar bridge giving TypeScript extensions the Pi API |

## Important entry points

- [`AGENTS.md`](../../../AGENTS.md): hard guardrails, build/test commands, architecture
  boundaries, TsBridge parity contract.
- [`docs/pisharp-developer-guide.md`](../../../docs/pisharp-developer-guide.md):
  canonical developer guide; authoritative for architecture and most current
  behavior.
- [`docs/specs/SDD-pi-csharp-port.md`](../../../docs/specs/SDD-pi-csharp-port.md):
  original C# port spec; **historical** — the codebase has moved on
  (Spectre.Console -> Terminal.Gui 2.0, NativeAOT -> dotnet global tool,
  `[AgentTool]` -> `IAgentTool`, jiti -> manifest shim generator).
- [`docs/adr/`](../../../docs/adr/): architecture decision records (only one exists:
  `2026-08-14-daemon-client-architecture.md`).

## Dependencies and consumers

### Depends on

- Nothing outside the repository; the SDK is a .NET 10 SDK (`net10.0`).

### Consumed by

- All other project skills (they route from this overview).

### External systems

- Node.js (for TypeScript extension loading via TsBridge).
- NuGet.org (release publishing via `dotnet nuget push`).
- Model providers (Anthropic, OpenAI, etc.) via `PiSharp.Ai` (see
  [model-providers](../model-providers/SKILL.md)).

## Invariants

The following must remain true:

1. `src/PiSharp.Abstractions` and `src/PiSharp.Agent.Core` remain dependency-light
   contract layers; they must not depend back on runtime/CLI/TUI or concrete
   providers/tools.
2. Runtime wiring stays in `src/PiSharp.Runtime`; composition concerns are not
   pushed into feature libraries.
3. Extension bridge/plugin host concerns stay inside `src/PiSharp.TsBridge` and
   `src/PiSharp.PluginHost`.
4. The top-level `javascript/` directory is never modified — it is reference-only
   source material from the original implementation.
5. New end-user functionality is implemented through extensions whenever possible;
   PiSharp core is extended only to unlock/improve extension capabilities.
6. PiSharp extension support remains backward compatible with extensions written
   for the original JavaScript version.

## Common change workflows

### Routing a change to the right place

1. Identify the affected concern (sessions, settings, tools, daemon, plugins...).
2. Load the matching skill from the [project router](../../SKILL.md).
3. If the change is new end-user functionality, check extension-first: can an
   extension implement it? Only extend core when it unlocks extension capability.

Files commonly changed together:

- `AGENTS.md` (guardrails change)
- `docs/pisharp-developer-guide.md` (architecture description change)

Validation:

```bash
dotnet build PiSharp.sln
dotnet test PiSharp.sln
```

## Testing and validation

Run for all changes in this area:

```bash
dotnet build PiSharp.sln
```

Run conditionally:

```bash
dotnet test PiSharp.sln
```

## Operational considerations

- Logs: `~/.pi/PiSharp/logs`. Settings: `~/.pi/PiSharp/settings.json`.
- Session files: `~/.pi/agent/sessions`.
- Agent npm extensions: `~/.pi/agent/npm`.

## Common mistakes

- Do not edit `javascript/` — ask for explicit confirmation first; it is
  reference-only.
- Do not add product-specific behavior directly into core/runtime/CLI/TUI when
  an extension can carry it.
- Do not treat `docs/specs/SDD-pi-csharp-port.md` or `docs/analysis/current-state-catalog.md`
  as current descriptions — both are stale in places (see below).
- Do not add a project to `PiSharp.sln` manually without checking the 4 solution
  folders' conventions; the webapp and TypeScript extensions are intentionally
  not members.

## Legacy and deprecated patterns

- `[AgentTool]` attributes: replaced by `IAgentTool` (see
  [tools-and-commands](../tools-and-commands/SKILL.md)).
- NativeAOT publish: replaced by `dotnet tool install --global` packaging.
- `javascript/packages/*`: original JS implementation — reference only, not used
  for loading extensions (TypeScript extensions load via the Node sidecar bridge).

Do not copy these patterns into new code unless compatibility requires it.

## Existing authoritative documentation

- [`AGENTS.md`](../../../AGENTS.md)

  * Covers guardrails, extension-first policy, build/test commands, architecture
    boundaries, TsBridge parity contract, known pitfalls.
  * Treat as authoritative for boundaries and parity.
  * Does not cover per-concern implementation detail.

- [`docs/pisharp-developer-guide.md`](../../../docs/pisharp-developer-guide.md)

  * Covers the overall architecture, harness pipeline, daemon/client design,
    session model, provider model, extension system.
  * Treat as authoritative for architecture and current behavior.
  * Does not cover the full CLI surface — several flags/modes are missing
    (see settings-and-resources); update it when behavior changes.

- [`docs/specs/SDD-pi-csharp-port.md`](../../../docs/specs/SDD-pi-csharp-port.md)

  * Original port spec.
  * Stale: describes Spectre.Console, NativeAOT, `[AgentTool]`, jiti loading —
    all superseded. Use for intent/history, not current contracts.

- [`docs/analysis/current-state-catalog.md`](../../../docs/analysis/pisharp-current-state-catalog.md)

  * Covers JSONL session internals (§2.6) not documented elsewhere.
  * Stale in places (e.g. §11 claims the daemon is "not in main" — it is).

## Known ambiguity and technical debt

- Only one ADR exists; most architecture decisions are undocumented as ADRs.
- The developer guide omits part of the CLI surface and the `~/.pi/extensions`
  discovery line; treat the guide as a baseline and verify against code.
- `src/pisharp-session-webapp` has no `.csproj` and is not in the solution; its
  build/run story is npm-based and undocumented in the developer guide.

## Evidence and verification

This skill was verified against commit `646522ccc6edc48acc39e4545cd120af9f1dafba`.

Primary evidence:

- [`AGENTS.md`](../../../AGENTS.md)
- [`docs/pisharp-developer-guide.md`](../../../docs/pisharp-developer-guide.md)
- [`PiSharp.sln`](../../../PiSharp.sln) (95 entries: 52 src + 43 test + 4 solution folders)
- [`docs/adr/2026-08-14-daemon-client-architecture.md`](../../../docs/adr/2026-08-14-daemon-client-architecture.md)
