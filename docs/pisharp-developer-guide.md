# PiSharp Developer Guide

PiSharp is the C#/.NET port of the Pi coding agent. This guide is the high-level map for PiSharp: what it does, how the major projects fit together, and where to find deeper documentation for each category.

## Detailed guides

| Topic | Details |
| --- | --- |
| [Runtime, settings, resources, CLI, and sessions](pisharp-runtime.md) | Startup sequence, settings precedence, runtime paths, resource discovery, CLI mode selection, session behavior, and troubleshooting. |
| [Tools](pisharp-tools.md) | Built-in tools, `IAgentTool`, schemas, results, execution modes, extension tool registration, and tool middleware/events. |
| [Slash command development](pisharp-slash-command-development.md) | Built-in slash-command catalog pattern, command-class structure, registry composition, alias grouping, testing, and extension points. |
| [TUI shortcut development](pisharp-tui-shortcut-development.md) | Built-in TUI shortcut catalog pattern, per-shortcut command classes, header hints, registrar/dispatcher composition, testing, and extension points. |
| [TUI tracing and profiling](pisharp-tui-tracing.md) | Real PiSharp prompt-editor trace workflow, real-host test trace workflow, `dotnet-trace` setup, and trace-analysis heuristics for typing, layout, transcript, and completion hot paths. |
| [Native .NET extensions](pisharp-native-extensions.md) | `.dll` plugin discovery/loading, metadata, `IExtensionApi`, events, middleware, UI, providers, override policy, unload/reload notes, and pitfalls. |
| [TypeScript extension compatibility](pisharp-typescript-extensions.md) | Node sidecar bridge, descriptor cache, C# manifest parity contract, runtime actions/snapshots, TypeScript tool/provider/event/UI proxying, limitations, and troubleshooting. |
| [Model providers](pisharp-providers.md) | `IModelProvider`, built-in providers, model catalog/selection, credentials, extension providers, and provider events. |
| [Agent coordination](pisharp-agent-coordination.md) | Multi-agent coordination: daemon, tools (`coordination_roster`, `coordination_send`, `coordination_inbox`), soft conflict warnings, subagent event observation, and agent definition frontmatter. |
| [PiSharp vs TypeScript Pi](pisharp-vs-pi.md) | Compatibility points, architectural differences, settings/session differences, extension model differences, and migration guidance. |

## What PiSharp does

PiSharp is an agent runtime and CLI for interactive and non-interactive coding-agent workflows. It provides:

- A command-line application (`pisharp`) with interactive TUI, print, JSON, and RPC modes.
- A streaming agent harness that sends conversation state to model providers, receives assistant message events, executes tool calls, and persists session history.
- Built-in tools for file and shell work: `read`, `bash`, `edit`, `write`, `grep`, `find`, and `ls`.
- Multi-provider LLM support through a shared `IModelProvider` abstraction.
- JSONL-backed session storage with session continuation, forking, compaction, labels, model changes, and thinking-level changes.
- Resource discovery for extensions, skills, prompt templates, themes, packages, system prompts, append prompts, and context files.
- Two extension paths:
  - Native .NET extensions loaded from `.dll` assemblies.
  - TypeScript Pi extensions loaded through an out-of-process Node.js bridge.
- A lightweight ASP.NET Core server project with health and WebSocket endpoints for live session use cases.

## Repository layout

| Project | Role |
| --- | --- |
| `src/PiSharp.Abstractions` | Cross-cutting abstractions for execution environment, filesystem, shell, sessions, streaming, messages, and result types. |
| `src/PiSharp.Agent.Core` | Core agent contracts: model descriptors, agent events, assistant streaming events, tool contracts, prompt interfaces, and loop configuration. |
| `src/PiSharp.Agent` | Agent harness, loop, compaction, session implementations, JSON serialization, system prompt composition, skill loading, theme documents, and prompt templates. |
| `src/PiSharp.Ai` | Provider abstraction, built-in providers, model registry/catalog, credential resolution, HTTP/SSE helpers, OAuth storage, and model generation. |
| `src/PiSharp.Tools` | Built-in tool implementations and shared helpers such as mutation queues, truncation, path utilities, and schema generation. |
| `src/PiSharp.Extensions` | Native extension API, extension registry, extension events, middleware, UI abstraction, and registration contracts. |
| `src/PiSharp.PluginHost` | Native `.dll` plugin discovery/loading with collectible `AssemblyLoadContext`. |
| `src/PiSharp.TsBridge` | Node sidecar host, JSON-RPC transport, TypeScript extension descriptor caching, manifest-generated compatibility shims, JS parity API-surface manifest, TS tool/provider adapters, and bridge protocol contracts. |
| `src/PiSharp.Compatibility` | Compatibility paths, settings, package/resource loading, and Pi-compatible session/resource conventions. |
| `src/PiSharp.Coordination` | Native extension for multi-agent coordination: repo daemon, agent roster, chat messaging, file activity tracking, soft conflict warnings, and subagent event observation. |
| `src/PiSharp.Runtime` | Runtime bootstrap that wires settings, providers, resources, sessions, tools, extensions, prompts, and the agent harness together. |
| `src/PiSharp.Cli` | CLI parser, help text, runtime option mapping, interactive mode, print mode, and RPC mode. |
| `src/PiSharp.Tui` | Terminal UI components: chat view, prompt editor, dialogs, footer/header, diff view, selector, session tree, and rendering helpers. |
| `src/PiSharp.Server` | ASP.NET Core host with `/health` and `/ws` endpoints. |
| `tests/*` | Project-specific test suites. |

## Runtime architecture overview

At startup, `PiRuntimeBootstrap.CreateRuntimeAsync()` builds a `SessionRuntime` by loading settings, registering providers, discovering resources, resolving the session, creating tools, loading extensions, selecting model/thinking settings, composing the system prompt, creating the agent harness, and dispatching `session_start`.

The main runtime object is `SessionRuntime`. It owns the session repo, current session, harness, extension manager, native plugin host, optional TypeScript host, settings snapshot, selected model, loaded resources, loaded skills, prompt template catalog, theme document, and startup diagnostics.

See [Runtime, settings, resources, CLI, and sessions](pisharp-runtime.md) for details.

## Agent harness and event flow

`AgentHarness<TMetadata>` coordinates turns. It is responsible for:

- Maintaining the current session and model selection.
- Tracking current phase (`Idle`, turn, compaction, etc.).
- Queueing steering, follow-up, and next-turn messages.
- Registering/unregistering runtime tools.
- Building the system prompt for a turn.
- Running the agent loop and tool calls.
- Persisting session entries.
- Publishing events to extensions and UI/listeners.
- Running compaction and branch summarization.

Loop event processing uses a durability-first pipeline:

1. `PersistenceStage`
2. `PhaseTransitionStage`
3. `ToolMiddlewareStage`
4. `ExtensionDispatchStage`
5. `ListenerNotificationStage`

This ordering keeps session writes ahead of extension/listener notification.

The runtime keeps hot event consumers off the harness critical path where possible. Ordinary TypeScript bridge event forwarding is queued behind `SessionRuntime`; mutating extension hooks still run synchronously because their return values affect behavior. The TUI harness subscription batches short event bursts and reduces them once per batch before scheduling a render. Chat rows are reused when transcript and bridge-slot inputs are unchanged, which keeps prompt-only and working-indicator frames cheap.

Runtime snapshots exposed to TypeScript extensions are cached in `RuntimeExtensionBinder` and invalidated by cheap runtime keys such as session leaf, model selection, tool state, thinking level, and extension registry changes. Do not add snapshot fields with hard-coded fallbacks; wire live data and include cache invalidation inputs when the field can change independently.

Important extension-visible events include session, agent/turn/message, provider/model, tool, compaction, queue, save-point, abort, settled, and resource-update events. See [Native .NET extensions](pisharp-native-extensions.md) and [TypeScript extension compatibility](pisharp-typescript-extensions.md).

## Built-in tools overview

Built-in tools are created by `BuiltInTools`:

- `read` - read files.
- `bash` - execute shell commands through the configured `IExecutionEnv`.
- `edit` - apply exact-text file edits.
- `write` - create or overwrite files.
- `grep` - search file contents.
- `find` - find files by pattern.
- `ls` - list directories.

All tools implement `IAgentTool` and return model-visible content plus structured details. Extensions can register additional tools or intentionally override existing ones. See [Tools](pisharp-tools.md).

## Extension overview

PiSharp supports two extension paths:

- **Native .NET extensions** are compiled `.dll` plugins. They run in-process and use `IExtensionApi` directly.
- **TypeScript extensions** are Pi-compatible modules loaded through a Node.js sidecar and JSON-RPC bridge.

Both extension paths register into the same extension registry, so tools, commands, shortcuts, flags, prompt contributions, providers, and event handlers participate in one runtime surface.

For TypeScript bridge work, treat `src/PiSharp.TsBridge/TsBridgeManifestFactory.cs` as the authoritative C# parity contract. Manifest entries must correspond to real generated shim behavior, runtime actions, or live runtime snapshot fields. Do not use roadmap statuses or broad stubs for missing JavaScript Pi APIs.

See:

- [Native .NET extensions](pisharp-native-extensions.md)
- [TypeScript extension compatibility](pisharp-typescript-extensions.md)
- [Agent coordination](pisharp-agent-coordination.md) — a concrete native extension for multi-agent communication

## Provider overview

Providers implement `IModelProvider` with streaming and completion methods. Built-in provider areas include Anthropic, OpenAI completions/responses, Google, Google Vertex, Bedrock, Mistral, and Faux/test provider support.

Native extensions can register providers directly. TypeScript extensions can register bridged providers. See [Model providers](pisharp-providers.md).

## Compatibility overview

PiSharp preserves Pi-compatible locations and concepts where practical:

- Legacy settings under `~/.pi/agent/settings.json` and `<cwd>/.pi/settings.json`.
- Sessions under `~/.pi/agent/sessions` by default.
- Pi-style TypeScript extension, skill, package, context, and prompt conventions.
- JSONL session compatibility by default.

PiSharp adds native .NET extension support, PiSharp-specific settings layers, descriptor caching for TypeScript extensions, and ASP.NET Core hosting. See [PiSharp vs TypeScript Pi](pisharp-vs-pi.md).

## Development notes

- Documentation-only changes do not require running the full build.
- For code changes, prefer `dotnet test` for the affected test project(s) and inspect LSP diagnostics first.
- The TypeScript reference implementation under `javascript/` is useful for compatibility research, but PiSharp source is the authority for current behavior.
- `docs/specs/PRD-pi-csharp-port.md` and `docs/specs/SDD-pi-csharp-port.md` describe the original port intent; the source tree is the authority for current behavior.
