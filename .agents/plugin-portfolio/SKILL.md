---
name: plugin-portfolio
description: >
  Use when working inside a shipped PiSharp plugin (P07-P31): memory, plan
  mode, permissions, agent messaging, research, LSP/DAP, MCP, eval,
  observability, model roles, internal URLs, declarative tools, continuity,
  foreign compatibility, browser, git, advisor, SDK, AST, subagents, continual
  harness, packages/skills. Covers which plugin owns which feature, shared
  plugin conventions, and implementation status tracking.
type: domain
scope:
  - src/PiSharp.Memory/**
  - src/PiSharp.PlanMode/**
  - src/PiSharp.Permissions/**
  - src/PiSharp.AgentMessaging/**
  - src/PiSharp.Research/**
  - src/PiSharp.Plugins.Lsp/**
  - src/PiSharp.Plugins.Debug/**
  - src/PiSharp.Mcp/**
  - src/PiSharp.Eval/**
  - src/PiSharp.Telemetry.Otlp/**
  - src/PiSharp.ModelRoles/**
  - src/PiSharp.InternalUrls/**
  - src/PiSharp.DeclarativeTools/**
  - src/PiSharp.Continuity/**
  - src/PiSharp.Plugins.ForeignCompat/**
  - src/PiSharp.Browser/**
  - src/PiSharp.Git/**
  - src/PiSharp.Advisor/**
  - src/PiSharp.Sdk/**
  - src/PiSharp.Ast/**
  - src/PiSharp.Subagents/**
  - src/PiSharp.ContinualHarness/**
  - src/PiSharp.Packages/**
  - docs/pisharp-plugins.md
  - docs/pisharp-implementation-status.md
related_skills:
  - extension-platform
  - agent-harness
  - daemon-protocol
  - repository-overview
last_verified:
  commit: "646522ccc6edc48acc39e4545cd120af9f1dafba"
  date: "2026-08-14"
confidence: high
---

# Plugin Portfolio (P07-P31)

## When to use this skill

Use this skill when:

- modifying a shipped plugin (memory, plan mode, permissions, MCP, LSP, eval, ...);
- figuring out which plugin owns a feature;
- adding a plugin to the portfolio;
- checking implementation/test status of a plugin;
- changing a plugin's daemon-facing surface (theme registry, plan-mode RPC,
  mcp_status, metrics, package/skill commands).

Typical tasks include:

- changing memory storage or retrieval behavior;
- extending plan-mode phases;
- adding a new MCP server integration;
- updating `docs/pisharp-implementation-status.md` for a plugin.

Do not use this skill for:

- the shared extension API/lifecycle — use [extension-platform](../extension-platform/SKILL.md);
- the TsBridge parity contract — use [tsbridge-parity](../tsbridge-parity/SKILL.md);
- the agent loop pipeline — use [agent-harness](../agent-harness/SKILL.md).

## Responsibilities and boundaries

This area owns:

- each shipped plugin's feature behavior;
- shared plugin authoring conventions;
- plugin status tracking in `docs/pisharp-implementation-status.md`.

This area does not own:

- the extension platform itself (loading, IExtensionApi) — extension-platform;
- daemon wire protocol mechanics — daemon-protocol;
- built-in (non-extension) tools — tools-and-commands.

## Architecture

Each plugin is a net10.0 class library under `src/` implementing `IExtension`
with `[ExtensionMetadata("id")]`, referencing `PiSharp.Extensions`/
`PiSharp.Agent.Core`/`PiSharp.Abstractions` (never Runtime/Cli/Tui). Plugins
register their surface through `IExtensionApi` (see
[extension-platform](../extension-platform/SKILL.md)).

Several plugins expose daemon-facing surfaces that ride the wire protocol
(see [daemon-protocol](../daemon-protocol/SKILL.md)):

- theme registry (`list_themes`/`set_theme`/`theme_changed`);
- plan-mode RPC (`set_plan_mode`/`get_plan_mode`/`plan_mode_changed`);
- telemetry aggregates (`get_metrics`, `get_session_stats`);
- package/skill management (`install_extension`, `update_extension`,
  `remove_extension`, `manage_skill`, `get_skills`);
- MCP status (`mcp_status`);
- continuity/session services (`IContinuitySessionService`).

### Important components

| Component | Location | Responsibility |
|---|---|---|
| Memory | `src/PiSharp.Memory` | Working/context memory store |
| Plan mode | `src/PiSharp.PlanMode` | Planning/executing/aborting phases |
| Permissions | `src/PiSharp.Permissions` | Tool/action permission checks |
| Agent messaging | `src/PiSharp.AgentMessaging` | Inter-agent messaging |
| Research | `src/PiSharp.Research` | Web/search providers |
| LSP/DAP | `src/PiSharp.Plugins.Lsp`, `src/PiSharp.Plugins.Debug` | Language server / debugger integrations |
| MCP | `src/PiSharp.Mcp` | MCP client + server status |
| Eval | `src/PiSharp.Eval` | Evaluation harness |
| Observability | `src/PiSharp.Telemetry.Otlp` | OTLP telemetry |
| Model roles | `src/PiSharp.ModelRoles` | Role-based model selection |
| Internal URLs | `src/PiSharp.InternalUrls` | Internal URL scheme handling |
| Declarative tools | `src/PiSharp.DeclarativeTools` | Declarative tool definitions |
| Continuity | `src/PiSharp.Continuity` | Session continuity service |
| Foreign compat | `src/PiSharp.Plugins.ForeignCompat` | JS Pi foreign compatibility |
| Browser | `src/PiSharp.Browser` | Browser automation |
| Git | `src/PiSharp.Git` | Git operations |
| Advisor | `src/PiSharp.Advisor` | Advisor notes/event lane |
| SDK | `src/PiSharp.Sdk` | Programmatic SDK surface |
| AST | `src/PiSharp.Ast` | AST tooling |
| Subagents | `src/PiSharp.Subagents` | Subagent spawning/coordination |
| Continual harness | `src/PiSharp.ContinualHarness` | Continual agent harness |
| Packages/skills | `src/PiSharp.Packages` | Package + managed-skill support |

### Main flow

1. Runtime loads plugins through the extension platform.
2. Each plugin registers tools/commands/hooks via `IExtensionApi`.
3. Plugins with daemon-facing surfaces register RPC/command handlers that the
   daemon dispatches over the wire protocol.

## Project terminology

| Term | Meaning in this repository |
|---|---|
| Plugin | A shipped native extension in the portfolio |
| P07-P31 | Portfolio numbering used in `docs/pisharp-implementation-status.md` |
| Plan mode | The plugin-owned planning/executing machine |
| mcp_status | Daemon command exposing MCP server status |
| Advisor event lane | The daemon event channel carrying advisor notes |

## Important entry points
- [`skills/SKILL.md`](../../SKILL.md): project router — routing index for all PiSharp project skills.


- [`docs/pisharp-plugins.md`](../../../docs/pisharp-plugins.md): portfolio map.
- [`docs/pisharp-implementation-status.md`](../../../docs/pisharp-implementation-status.md):
  P01-P31 status + test evidence.
- Each plugin project under `src/PiSharp.*`.

## Dependencies and consumers

### Depends on

- `src/PiSharp.Extensions`, `src/PiSharp.Agent.Core`, `src/PiSharp.Abstractions`.

### Consumed by

- The daemon (wire surfaces), the harness (tools/hooks), the CLI/TUI
  (commands/shortcuts).

### External systems

- LSP/DAP servers, MCP servers, browser engines, model APIs, npm (packages),
  OTLP collectors.

## Invariants

The following must remain true:

1. Every plugin is a net10.0 class library with `[ExtensionMetadata("id")]` and a
   concrete `IExtension` implementation.
2. Plugins reference only `Extensions`/`Agent.Core`/`Abstractions` — never
   Runtime/Cli/Tui.
3. New plugin status is recorded in `docs/pisharp-implementation-status.md`.
4. Plugin behavior stays backward compatible with JS-Pi-compatible extension
   contracts.

## Common change workflows

### Modify a plugin

1. Load [extension-platform](../extension-platform/SKILL.md) for lifecycle context.
2. Change the plugin project under `src/<Plugin>`.
3. Add/adjust tests in `tests/<Plugin>.Tests`.
4. Update `docs/pisharp-plugins.md` and
   `docs/pisharp-implementation-status.md` status rows.

Files commonly changed together:

- `src/<Plugin>/**`
- `tests/<Plugin>.Tests/**`
- `docs/pisharp-implementation-status.md`

Validation:

```bash
dotnet build PiSharp.sln
dotnet test tests/<Plugin>.Tests/<Plugin>.Tests.csproj
```

### Add a plugin to the portfolio

Follow the add-native-extension workflow in
[extension-platform](../extension-platform/SKILL.md), then add the plugin to
`docs/pisharp-plugins.md` and assign the next P-number in
`docs/pisharp-implementation-status.md`.

## Testing and validation

Run for all changes in this area:

```bash
dotnet build PiSharp.sln
dotnet test tests/<Plugin>.Tests/<Plugin>.Tests.csproj
```

Run conditionally:

```bash
dotnet test PiSharp.sln
```

## Operational considerations

- Some plugins integrate external systems (MCP servers, browsers, LSP/DAP);
  their tests may require those systems or use mocks — check the plugin's test
  project before assuming full coverage.
- `docs/pisharp-implementation-status.md` is the evidence ledger; keep it in
  sync when a plugin's capabilities or tests change.

## Common mistakes

- Do not put product-specific behavior into core when a plugin can carry it
  (extension-first policy).
- Do not forget `[ExtensionMetadata("id")]` on new plugins.
- Do not reference Runtime/Cli/Tui from a plugin project.
- Do not assume a plugin's daemon surface exists without checking
  `ServerContracts.cs` (see daemon-protocol).

## Legacy and deprecated patterns

- Original JS Pi plugin loading (`javascript/packages/*`) is reference-only;
  PiSharp plugins are native or TypeScript extensions.

## Existing authoritative documentation

- [`docs/pisharp-plugins.md`](../../../docs/pisharp-plugins.md)

  * Covers the plugin portfolio map.
  * Treat as authoritative for which plugin owns a feature.

- [`docs/pisharp-implementation-status.md`](../../../docs/pisharp-implementation-status.md)

  * Covers P01-P31 status and test evidence.
  * Treat as authoritative for status; note some wording can lag the code
    (e.g. one entry implies `src/PiSharp.Git` was removed — it exists; only a
    CLI slash command file was removed).

## Known ambiguity and technical debt

- The portfolio has grown organically; some plugin responsibilities overlap
  (research vs web tools, packages vs skills). When ambiguous, check
  `docs/pisharp-plugins.md` first, then the plugin's `IExtension` registration.
- Continuity/foreign-compat plugins intentionally mirror JS Pi behavior; their
  boundaries are compatibility-driven, not capability-driven.

## Evidence and verification

This skill was verified against commit `646522ccc6edc48acc39e4545cd120af9f1dafba`.

Primary evidence:

- [`docs/pisharp-plugins.md`](../../../docs/pisharp-plugins.md)
- [`docs/pisharp-implementation-status.md`](../../../docs/pisharp-implementation-status.md)
- [`src/PiSharp.Server/Contracts/ServerContracts.cs`](../../../src/PiSharp.Server/Contracts/ServerContracts.cs)
  (daemon-facing plugin surfaces)
- [`src/PiSharp.Extensions/IExtensionApi.cs`](../../../src/PiSharp.Extensions/IExtensionApi.cs)
