---
name: tools-and-commands
description: >
  Use when adding or changing built-in tools and slash commands: IAgentTool
  implementations, BuiltInTools.CreateAll/CreateReadOnly registration, tool
  schema generation (JsonTool/ToolSchemas), tool middleware, slash command
  catalog registration, and extension tool/command registration. Covers the
  tools the agent can call and the commands users can type.
type: cross-cutting
scope:
  - src/PiSharp.Tools/**
  - src/PiSharp.Cli/Commands/**
  - src/PiSharp.Agent.Core/Tools/AgentToolContracts.cs
  - src/PiSharp.Extensions/**
  - docs/pisharp-tools.md
  - docs/pisharp-slash-command-development.md
related_skills:
  - extension-platform
  - agent-harness
  - tsbridge-parity
last_verified:
  commit: "646522ccc6edc48acc39e4545cd120af9f1dafba"
  date: "2026-08-14"
confidence: high
---

# Built-in Tools and Slash Commands

## When to use this skill

Use this skill when:

- adding or changing a built-in tool (read, bash, edit, write, grep, find, ls);
- changing tool registration (full vs read-only sets);
- changing tool schema generation;
- adding tool middleware;
- adding a slash command to the catalog;
- registering tools/commands from an extension.

Typical tasks include:

- implementing an `IAgentTool`;
- adding a tool to `BuiltInTools.CreateAll`;
- adding a `JsonTool<TParameters,TDetails>`;
- adding a slash command entry;
- changing which tools are available in read-only mode.

Do not use this skill for:

- the extension API lifecycle — use [extension-platform](../extension-platform/SKILL.md);
- the agent loop pipeline — use [agent-harness](../agent-harness/SKILL.md);
- the TsBridge parity surface — use [tsbridge-parity](../tsbridge-parity/SKILL.md).

## Responsibilities and boundaries

This area owns:

- built-in tool implementations;
- tool registration sets (full vs read-only);
- tool schema generation;
- slash command catalog and registration;
- extension tool/command registration surfaces.

This area does not own:

- how the harness invokes tools (agent-harness);
- how tools are exposed to TypeScript extensions (tsbridge-parity).

## Architecture

Built-in tools live in `src/PiSharp.Tools`:

- `BuiltInTools.CreateAll` registers the full set: read, bash, edit, write,
  grep, find, ls.
- `BuiltInTools.CreateReadOnly` registers the read-only subset: read, grep,
  find, ls.

Tools implement `IAgentTool` (contract in
`src/PiSharp.Agent.Core/AgentToolContracts.cs`) and return content plus
structured details. `JsonTool<TParameters,TDetails>` plus `ToolSchemas` provide
JSON-schema generation for tool parameters/results. Tool middleware runs in
the harness's `ToolMiddlewareStage` (see
[agent-harness](../agent-harness/SKILL.md)).

Slash commands follow a catalog pattern in `src/PiSharp.Cli/Commands`
(`BuiltInSlashCommandCatalog` + registry factory). Extensions register tools
via `IExtensionApi.RegisterTool` and commands via `RegisterCommand` (see
[extension-platform](../extension-platform/SKILL.md)).

### Important components

| Component | Location | Responsibility |
|---|---|---|
| Built-in tools | `src/PiSharp.Tools/BuiltInTools.cs` | `CreateAll` / `CreateReadOnly` registration |
| Tool contract | `src/PiSharp.Agent.Core/AgentToolContracts.cs` | `IAgentTool`, `JsonTool<TParameters,TDetails>`, `ToolSchemas` |
| Slash commands | `src/PiSharp.Cli/Commands/` | `BuiltInSlashCommandCatalog` + registry factory |
| Extension registration | `src/PiSharp.Extensions/IExtensionApi.cs` | `RegisterTool`, `RegisterCommand` |

### Main flow

1. Harness requests tools from the tool registry (full or read-only set).
2. The agent calls a tool; the tool executes and returns content + details.
3. Tool middleware runs around execution (harness pipeline).
4. Users type slash commands; the CLI catalog dispatches them.

## Project terminology

| Term | Meaning in this repository |
|---|---|
| IAgentTool | Tool contract (replaces legacy `[AgentTool]` attributes) |
| JsonTool<TParameters,TDetails> | JSON-schema-driven tool base |
| ToolSchemas | JSON-schema generation helper |
| CreateAll / CreateReadOnly | Full vs read-only tool registration sets |
| Slash command | User-typed `/command` dispatched by the CLI catalog |

## Important entry points
- [`skills/SKILL.md`](../../SKILL.md): project router — routing index for all PiSharp project skills.


- [`src/PiSharp.Tools/BuiltInTools.cs`](../../../src/PiSharp.Tools/BuiltInTools.cs)
- [`src/PiSharp.Agent.Core/AgentToolContracts.cs`](../../../src/PiSharp.Agent.Core/Tools/AgentToolContracts.cs)
- [`src/PiSharp.Cli/Commands/`](../../../src/PiSharp.Cli/Commands/)
- [`docs/pisharp-tools.md`](../../../docs/pisharp-tools.md)
- [`docs/pisharp-slash-command-development.md`](../../../docs/pisharp-slash-command-development.md)

## Dependencies and consumers

### Depends on

- `src/PiSharp.Agent.Core` (contracts), `src/PiSharp.Tools` internals.

### Consumed by

- `AgentHarness` (tool invocation), the CLI (slash commands), extensions
  (registration), TsBridge (tool metadata exposure).

### External systems

- Tool backends (shell for bash, filesystem for read/edit/write, etc.).

## Invariants

The following must remain true:

1. Read-only mode must not expose mutating tools — `CreateReadOnly` excludes
   bash/edit/write.
2. Every tool returns content plus structured details.
3. Tools register through the registry — no ad-hoc invocation paths.
4. Slash commands register through the catalog — no hardcoded dispatch chains
   outside it.
5. Extension-registered tools/commands use `IExtensionApi.RegisterTool` /
   `RegisterCommand`.

## Common change workflows

### Add a built-in tool

1. Implement `IAgentTool` in `src/PiSharp.Tools`.
2. Register it in `BuiltInTools.CreateAll` (and `CreateReadOnly` only if it is
   read-only-safe).
3. Use `JsonTool<TParameters,TDetails>` + `ToolSchemas` for schema generation.
4. Add tests covering parameters, schema, and execution.

Files commonly changed together:

- `src/PiSharp.Tools/**`
- `tests/PiSharp.Tools.Tests/**`

Validation:

```bash
dotnet build PiSharp.sln
dotnet test tests/PiSharp.Tools.Tests/PiSharp.Tools.Tests.csproj
```

### Add a slash command

1. Follow the catalog pattern in `docs/pisharp-slash-command-development.md`.
2. Add the command to `BuiltInSlashCommandCatalog` + registry factory.
3. Add tests for dispatch and argument handling.

Files commonly changed together:

- `src/PiSharp.Cli/Commands/**`
- `tests/PiSharp.Cli.Tests/**` (or the matching test project)
- `docs/pisharp-slash-command-development.md`

Validation:

```bash
dotnet test tests/PiSharp.Cli.Tests/PiSharp.Cli.Tests.csproj
```

### Register a tool from an extension

Use `IExtensionApi.RegisterTool` (see
[extension-platform](../extension-platform/SKILL.md)); if TypeScript extensions
must call it, wire the TsBridge parity layers (see
[tsbridge-parity](../tsbridge-parity/SKILL.md)).

## Testing and validation

Run for all changes in this area:

```bash
dotnet build PiSharp.sln
dotnet test tests/PiSharp.Tools.Tests/PiSharp.Tools.Tests.csproj
```

Run conditionally:

```bash
dotnet test tests/PiSharp.Cli.Tests/PiSharp.Cli.Tests.csproj
dotnet test PiSharp.sln
```

## Operational considerations

- Bash tool execution is a security-sensitive surface — parameterized schemas
  and permission checks (permissions plugin) apply; never bypass them.
- Tool metadata (execution mode, argument preparation) is exposed over TsBridge
  for extensions; changing it affects parity tests.

## Common mistakes

- Do not add a mutating tool to `CreateReadOnly`.
- Do not bypass the registry with direct tool invocation in new code.
- Do not use legacy `[AgentTool]` attributes — implement `IAgentTool`.
- Do not hardcode slash-command dispatch outside the catalog.

## Legacy and deprecated patterns

- `[AgentTool]` attributes: replaced by `IAgentTool`. Do not copy into new code.

## Existing authoritative documentation

- [`docs/pisharp-tools.md`](../../../docs/pisharp-tools.md)

  * Covers built-in tools and contracts.
  * Treat as authoritative for the tool list; verify current set in
    `BuiltInTools.cs`.

- [`docs/pisharp-slash-command-development.md`](../../../docs/pisharp-slash-command-development.md)

  * Covers the slash command catalog pattern.
  * Treat as authoritative for adding commands.

## Known ambiguity and technical debt

- The built-in tool list may drift from the doc; `BuiltInTools.cs` is the
  source of truth.
- Slash command catalog vs extension-registered commands have two registration
  paths; keep the distinction when routing.

## Evidence and verification

This skill was verified against commit `646522ccc6edc48acc39e4545cd120af9f1dafba`.

Primary evidence:

- [`src/PiSharp.Tools/BuiltInTools.cs`](../../../src/PiSharp.Tools/BuiltInTools.cs)
- [`src/PiSharp.Agent.Core/AgentToolContracts.cs`](../../../src/PiSharp.Agent.Core/Tools/AgentToolContracts.cs)
- [`docs/pisharp-tools.md`](../../../docs/pisharp-tools.md)
- [`docs/pisharp-slash-command-development.md`](../../../docs/pisharp-slash-command-development.md)
- [`src/PiSharp.Extensions/IExtensionApi.cs`](../../../src/PiSharp.Extensions/IExtensionApi.cs)
