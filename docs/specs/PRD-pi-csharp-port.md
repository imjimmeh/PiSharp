# pi C# Port - Product Requirements Document

| Metadata | |
|----------|-|
| **Version** | 1.0.0 |
| **Status** | Draft |
| **Date** | 2026-05-21 |
| **Authors** | pi-core-team |

---

## 1. Executive Summary

The pi-coding-agent TypeScript project is a modular AI agent framework with an agent loop, multi-provider LLM support, a rich extension system, session management, and a CLI with interactive (TUI), RPC, and print modes. This PRD defines the port of this codebase to C# to achieve improved runtime performance, access the broader .NET ecosystem, and enable a more robust native extensibility model via AssemblyLoadContext plugin loading.

The port must maintain **full backwards compatibility** with existing TypeScript extensions through an out-of-process sidecar bridge. It must also deliver a C#-native extensibility surface that feels idiomatic to .NET developers (attribute-based tool discovery, middleware pipeline, interface-based plugin contracts).

---

## 2. Goals

- **G1**: Port all core functionality — agent loop, built-in tools, session management, LLM provider abstraction, and CLI — to C#.
- **G2**: Maintain full backwards compatibility with existing TypeScript extensions via an out-of-process JSON-RPC bridge running a Node.js sidecar.
- **G3**: Deliver C#-native extensibility: attribute-based tool discovery, `AssemblyLoadContext` plugin loading, and a middleware pipeline for agent events.
- **G4**: Support all existing CLI modes: interactive TUI, RPC (JSON-line stdin/stdout), and print (non-interactive).
- **G5**: Support all existing LLM providers (Anthropic, OpenAI, Google, Bedrock, Mistral, etc.) with the same unified abstraction.

---

## 3. Non-Goals

- **NG1**: Porting the web-ui (`@earendil-works/pi-web-ui`) — remains a JavaScript/TypeScript project.
- **NG2**: Porting the TUI rendering engine (`@earendil-works/pi-tui`) in full — wrappers or minimal interop may be used; a full TUI port is not required initially.
- **NG3**: Real-time bi-directional sync with the TypeScript repository — the C# codebase will diverge and be independently maintained.
- **NG4**: Supporting TypeScript-extensions in-process — TS extensions run exclusively out-of-process via the sidecar bridge.

---

## 4. User Stories

| ID | As a... | I want to... | So that... |
|----|---------|--------------|------------|
| US1 | C# developer | Write a custom tool by decorating a method with `[AgentTool("name")]` | I can extend the agent without learning a plugin DSL |
| US2 | CLI user | Use all my existing TypeScript extensions without modification | I can migrate to the C# version without losing my tooling |
| US3 | Plugin author | Drop a compiled `.dll` into `plugins/` and have it auto-discovered | Distribution is as simple as copying a file |
| US4 | LLM provider integrator | Implement `IProvider` and register it via DI or attribute | I can add new model backends without forking the core |
| US5 | Extension author | Write middleware that intercepts events before/after LLM calls | I can implement logging, rate-limiting, or observability |
| US6 | Power user | Fork a session, navigate the tree, and compact history | I can experiment with different conversation paths |
| US7 | Developer | Use the agent via JSON-line protocol from any language | I can integrate pi into my own toolchain without a .NET dependency |

---

## 5. Functional Requirements

### FR1: Agent Loop

The C# agent loop must produce an `IAsyncEnumerable<AgentEvent>` (conceptual equivalent of the TS `EventStream`).

- **FR1.1**: Support sequential and parallel tool execution (configurable per turn).
- **FR1.2**: Support steering messages injected mid-conversation.
- **FR1.3**: Support follow-up messages after a turn completes.
- **FR1.4**: Support conversation compaction (summarize or prune older messages to fit context window).
- **FR1.5**: Support retry logic on provider failures.
- **FR1.6**: Emit all event types: `agent_start/end`, `turn_start/end`, `message_start/update/end`, `tool_execution_start/update/end`.

### FR2: Built-in Tools

All seven existing tools must be ported:

| Tool | Description |
|------|-------------|
| **bash** | Execute shell commands with timeout, working directory, environment variables |
| **read** | Read file contents with optional offset/limit and line number prefixing |
| **write** | Write content to a file (create or overwrite) |
| **edit** | Apply exact-string replacements in files with mutation queue (preview/commit/rollback) |
| **grep** | Regex-based content search across files |
| **find** | Glob pattern matching to locate files by name |
| **ls** | List directory contents |

Each tool must expose: typed input parameters, a description, an execute handler, and optional rendering hooks.

### FR3: LLM Provider Abstraction

- **FR3.1**: Define an `IProvider` interface with methods for chat completion and streaming.
- **FR3.2**: Support all existing providers: Anthropic, OpenAI, Google (Vertex AI + AI Studio), AWS Bedrock, Mistral, Azure OpenAI, Ollama, Groq, Together AI, OpenRouter, DeepSeek, Cohere, Perplexity, Fireworks AI, Replicate, ElevenLabs, Anyscale, Modal, Lepton AI, NLP Cloud, Cloudflare Workers AI, AI21 Labs, Voyage AI, Jina AI, GitHub Models.
- **FR3.3**: Support model discovery (list available models per provider).
- **FR3.4**: Support OAuth token refresh, API key management, and base URL configuration.
- **FR3.5**: Support streaming via `IAsyncEnumerable<AssistantMessageEvent>`.
- **FR3.6**: Support image inputs and image generation where the provider supports it.

### FR4: Session Storage

- **FR4.1**: File-based session storage using JSONL format (one JSON object per line per event).
- **FR4.2**: Tree-structured session hierarchy (parent-child fork relationships).
- **FR4.3**: Operations: create, fork, navigate, switch, get state, list messages, compact, export.

### FR5: TS Extension Bridge

- **FR5.1**: Launch a Node.js sidecar process that loads TS extensions using the same loader mechanism (`jiti`) as the original.
- **FR5.2**: Communicate via JSON-RPC over stdin/stdout (matching the original RPC protocol).
- **FR5.3**: The bridge must proxy: tool registration, event hooks (`on("turn_start", ...)`), command registration, provider registration, and custom messages.
- **FR5.4**: The bridge must handle sidecar lifecycle — start on demand, restart on crash, graceful shutdown on exit.

### FR6: C# Native Plugin System

- **FR6.1**: Plugins are compiled .NET assemblies loaded via `AssemblyLoadContext` with isolation.
- **FR6.2**: Tool discovery via `[AgentTool]` attribute on methods or classes:

  ```csharp
  [AgentTool("my-tool", "Does something useful")]
  public class MyTool : IAgentTool
  {
      public Task<ToolResult> ExecuteAsync(ToolContext ctx, CancellationToken ct) { ... }
  }
  ```

- **FR6.3**: Event middleware via `IAgentMiddleware`:

  ```csharp
  public class LoggingMiddleware : IAgentMiddleware
  {
      public async Task OnTurnStartAsync(TurnEvent evt, AgentContext ctx, CancellationToken ct)
      {
          Console.WriteLine($"Turn starting: {evt.Message}");
      }
  }
  ```

- **FR6.4**: Provider registration via `[AgentProvider]` attribute or `IProvider` interface.
- **FR6.5**: Plugin discovery scans `plugins/` directory at startup; plugins can also specify dependencies and load order.

### FR7: CLI

- **FR7.1**: Interactive mode with a TUI (terminal UI).
- **FR7.2**: RPC mode — JSON-line protocol on stdin/stdout supporting commands: `prompt`, `steer`, `follow_up`, `abort`, `new_session`, `get_state`, `set_model`, `bash`, `compact`, `switch_session`, `fork`, `get_messages`, `get_commands`, `help`.
- **FR7.3**: Print mode — non-interactive, single-prompt execution with output to stdout.
- **FR7.4**: CLI argument parsing: model selection, config path, session path, working directory, extension paths, provider overrides.

### FR8: Extension API Compatibility

The C# extension API must surface equivalents of the TS `ExtensionAPI`:

- **FR8.1**: `RegisterTool` — register tools (both native C# and proxied TS tools).
- **FR8.2**: `RegisterCommand` — register CLI commands.
- **FR8.3**: `RegisterShortcut` — register keyboard shortcuts (TUI mode).
- **FR8.4**: `RegisterFlag` — register custom CLI flags.
- **FR8.5**: `RegisterProvider` — register custom LLM providers.
- **FR8.6**: `On(event)` — hook into 25+ agent lifecycle events.
- **FR8.7**: `SendMessage` — emit custom messages into the event stream.

---

## 6. Non-Functional Requirements

| ID | Requirement | Target |
|----|-------------|--------|
| NFR1 | **Startup time** | Cold start < 500ms; warm start < 100ms |
| NFR2 | **Memory usage** | Idle < 50MB; active session < 200MB (without large context) |
| NFR3 | **LLM latency overhead** | < 10ms per provider call (excluding network) |
| NFR4 | **Plugin load time** | < 100ms per assembly loaded |
| NFR5 | **Cross-platform** | Windows (x64, arm64), macOS (x64, arm64), Linux (x64, arm64) |
| NFR6 | **Concurrent sessions** | Support 10+ simultaneous sessions |
| NFR7 | **Logging** | Structured logging (Serilog or Microsoft.Extensions.Logging) with levels: Debug, Info, Warn, Error |
| NFR8 | **Telemetry** | OpenTelemetry-compatible tracing for agent loop and provider calls |

---

## 7. Constraints

| ID | Constraint |
|----|------------|
| C1 | **Node.js required as sidecar** — the TS extension bridge depends on a Node.js runtime being available on the system |
| C2 | **No in-process TS execution** — all TypeScript extensions execute in the sidecar process; no V8/Napi interop |
| C3 | **JSONL file format** — session storage must use the exact same JSONL format as the TS version for forward/backward compatibility |
| C4 | **RPC protocol stability** — the JSON-line protocol must remain compatible with the TS version to allow shared tooling |
| C5 | **.NET version** — target .NET 8.0+ (Long Term Support release) |

---

## 8. Architecture Overview

```
┌─────────────────────────────────────────────────────┐
│                   pi (C# CLI)                       │
├─────────────────────────────────────────────────────┤
│  Agent Loop (IAsyncEnumerable<AgentEvent>)          │
│                                                     │
│  ┌─────────────┐  ┌──────────────┐  ┌────────────┐ │
│  │ Built-in    │  │ Native C#    │  │ Middleware  │ │
│  │ Tools       │  │ Plugins (ALC)│  │ Pipeline   │ │
│  │ (bash,read, │  │ [AgentTool]  │  │ (IAgent    │ │
│  │  write,...) │  │ [AgentCmd]   │  │  Middleware)│ │
│  └─────────────┘  └──────────────┘  └────────────┘ │
│                                                     │
│  ┌──────────────────────────────────────────────┐   │
│  │            TS Extension Bridge                │   │
│  │  (JSON-RPC ↔ Node.js sidecar ↔ jiti loader)  │   │
│  └──────────────────────────────────────────────┘   │
│                                                     │
│  ┌────────────┐  ┌────────────────┐  ┌───────────┐ │
│  │ Providers  │  │ Session Store  │  │ CLI Parser│ │
│  │ (IProvider)│  │ (JSONL + Tree) │  │ (3 modes) │ │
│  └────────────┘  └────────────────┘  └───────────┘ │
└─────────────────────────────────────────────────────┘
```

---

## 9. Key Technical Decisions (Future ADRs)

The following decisions will be formalized in Architecture Decision Records (ADRs):

1. **Event stream model**: `IAsyncEnumerable<AgentEvent>` vs `IObservable<AgentEvent>` vs channels.
2. **DI container**: `Microsoft.Extensions.DependencyInjection` vs a lightweight container.
3. **Serialization**: `System.Text.Json` for JSONL sessions and RPC protocol.
4. **TUI library**: Spectre.Console vs Terminal.GUI vs interop with `@earendil-works/pi-tui`.
5. **Logging**: Serilog vs `Microsoft.Extensions.Logging`.
6. **Assembly load context**: Custom `AssemblyLoadContext` per plugin with shared framework assembly unification.

---

## 10. Success Criteria

- All goals (G1–G5) are demonstrably met.
- All functional requirements (FR1–FR8) are implemented and pass integration tests.
- All non-functional requirements (NFR1–NFR8) meet their target metrics.
- All constraints (C1–C5) are satisfied.
- All existing TS extensions load and function correctly through the bridge (tested with a representative sample).
- Session files written by the C# version are readable by the TS version and vice versa.
- CI pipeline builds and tests on Windows, macOS, and Linux.
