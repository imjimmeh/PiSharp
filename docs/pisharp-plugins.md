# PiSharp Plugin Portfolio

PiSharp ships a portfolio of plugins implemented as native .NET extensions plus daemon-side
surfaces. Each plugin is a class library (`net10.0`) discovered by `PiSharp.PluginHost`, marked
with `[ExtensionMetadata("id")]`, and initialized through `IExtension.InitializeAsync(IExtensionApi, CancellationToken)`.

> Plugin authoring conventions (ALC rules, settings namespacing, registration contracts) are in
> [Native .NET extensions](pisharp-native-extensions.md). Design documents for every plugin live in
> `docs/plans/` (index: `docs/plans/2026-08-14-plugin-index.md`).

## Plugin model

- **Assembly-level metadata**: `[assembly: ExtensionMetadata("pisharp.memory", Name = "...", Version = "1.0.0")]`.
- **App-base contracts only**: shared provider interfaces live in app-base assemblies
  (`PiSharp.Extensions`, `PiSharp.Agent.Core`, `PiSharp.Memory.Abstractions`); a plugin's own
  internal interfaces live in the plugin. Plugins load into a collectible ALC and can be unloaded.
- **Settings namespacing**: `IExtensionApi.Settings` auto-prefixes keys with `extensions.<namespace>.`
  (`extensions.pisharp-memory.enabled`), and plugins read bare keys through the scoped settings API.
- **No solution edits**: plugins are added to `PiSharp.sln` centrally; each plugin owns its `src/`
  and `tests/` projects.

## Portfolio

| Plugin project(s) | Plan | What it does | Key surface used |
| --- | --- | --- | --- |
| `PiSharp.Advisor` | P16 | Advisor notes: completion API + `advisor_note` event, note classification and settings | `IExtensionApi.Completion`, `Events`, daemon `advisor_note` lane |
| `PiSharp.AgentMessaging` | P07 | Agent-to-agent messaging: roster, `agent_message` send/read tool, outbox persistence, `agent_message`/`agent_roster_update` client events | `Session`, `SendMessageAsync`, `EmitClientEventAsync` |
| `PiSharp.ContinualHarness` | P09 | Continual refinement: `/refine list\|show\|diff\|rollback\|sync`, journal-driven refinement loop, memory/skill write-back | `Events`, `Prompt`, `Files`, `Ui.ConfirmAsync` |
| `PiSharp.Continuity` + `PiSharp.Continuity.Contracts` | P23 | Continuity suite: `/goal`, `/heartbeat`, `/cron`, `/autonomous`; `IContinuitySessionService`; daemon wire commands `set_goal`/`get_goal`/`schedule_job`/`list_jobs`/`cancel_job`/`get_continuity_state` | `State`, `Session`, `Events`, server `ContinuityHandlers` |
| `PiSharp.DeclarativeTools` | P21 | Declarative tools from YAML/JSON definitions (schema, examples, execution env) | `IExtensionApi.ExecutionEnv`, `RegisterTool` |
| `PiSharp.Acp` | P13 | ACP (Agent Client Protocol) server: JSON-RPC loop, session manager, event translation, permission gate, content codec, approval modes | `SessionRuntime`, `AgentHarness`, `ExtensionMiddleware` |
| `PiSharp.Eval` + `PiSharp.Eval.Kernel.CSharp` | P15 | Eval/bench harness: `BenchRunner`, spec parser, result writer, `/kernel` commands, Roslyn C# kernel | `IExtensionToolApi.ExecuteToolAsync`, `RegisterCommand` |
| `PiSharp.Plugins.ForeignCompat` | P11 | Foreign rule providers: `.clinerules`, `.cursorrules`, `.github/copilot-instructions.md`, `GEMINIRULES.md`, repo `RULES.md` → `IRuleProvider` | `IExtensionApi.Rules`, `Settings` |
| `PiSharp.Extensions.Rules` | P10 | Rules engine: file + sticky `RULES.md` providers, frontmatter, regex trigger matching, TTSR stream-delta interceptor, `rules.always` prompt section, `--no-rules`/`--disable-sticky` | `IExtensionApi.Rules`, `StreamDelta`, `Prompt`, `RegisterFlag` |
| `PiSharp.InternalUrls` | P26 | Internal URL resolvers: `skill://`, `agent://`, `diff://` (with `DiffLedger` capture) | `IExtensionApi.Urls.RegisterResolver` |
| `PiSharp.Memory` + `PiSharp.Memory.Abstractions` + `PiSharp.Memory.Backends.File` + `PiSharp.Memory.Backends.Off` | P08 | Memory system: `/memory` command, `retain`/`recall`/`reflect`/`memory_edit`/`learn` tools, JSONL store + `memory_summary.md`, mental-model prompt injection, autolearn | `Settings`, `State`, `Prompt`, `RegisterTool`, `RegisterCommand` |
| `PiSharp.ModelRoles` | P20 | Model roles: role→provider/model resolution, `/roles` command, `modelRolesResolve`/`setModelRole` TS actions | `IExtensionApi.Model`, `Settings` |
| `PiSharp.Permissions` | P29 | Permissions gating: dangerous-op classification, allow/deny/ask matrix, grants, `permission_request` ui lane, audit events | `Use` middleware, `Settings`, `Events` |
| `PiSharp.PlanMode` | P14 | Plan mode: `/plan`, restricted tool set, planning model, plan capture + file store, input gate, prompt contributor; daemon `set_plan_mode`/`get_plan_mode` | `Tools.SetActiveToolsAsync`, `Model`, `EmitClientEventAsync`, server `PlanModeDaemonContracts` |
| `PiSharp.Plugins.Debug` | P12 | Debug adapter protocol: `DapClient`, `DapConnection`, session registry with idle sweep, managed DAP server | `RegisterCommand`, `RegisterTool` |
| `PiSharp.Plugins.Lsp` | P12 | Language server protocol clients: framing, handshake, diagnostics middleware, config interpolation | `RegisterTool`, `Settings` |
| `PiSharp.Plugins.ProtocolJsonRpc` | P12 | JSON-RPC 2.0 framed transport shared by LSP/DAP (`FramedJsonRpcConnection`, `RpcFrameShape`) | — (library) |
| `PiSharp.Research` + `PiSharp.Research.Search.Serper` + `.GoogleCse` + `.Brave` | P28 | Web research: `web_search` tool, search providers, PDF text extraction (PdfPig), `web` URL resolution | `IExtensionApi.Files`, `RegisterTool`, `Settings` |
| `PiSharp.Sdk` | P22 | Typed daemon client SDK: sessions, attach/replay, prompt/steer/fork/switch, UI-request lane, status | `PiSharp.Client`, `PiSharp.Agent.Core` |
| `PiSharp.Telemetry.Otlp` | P25 | OpenTelemetry export: OTLP HTTP exporter, install-id resolution | `IExtensionApi.Telemetry` |
| `PiSharp.Mcp` + `PiSharp.Mcp.Transports.Http` + `.Stdio` | P27 | MCP client: server config, sessions, tool adapters, slash command, HTTP/stdio transports | `RegisterTool`, `RegisterCommand` |
| `PiSharp.Packages` | P04 | Package management core (moved from `PiSharp.Cli`): npm/nuget registries, native installer, self-update methods | — (library consumed by CLI + runtime) |

## Daemon-side surfaces

The daemon host (`PiSharp.Server`) exposes these plugin-driven command surfaces (all additive to
`ServerContracts.cs`):

| Surface | Plan | Command types |
| --- | --- | --- |
| Extension package/skill commands | P04 | `install_extension`, `update_extension`, `uninstall_extension`, `list_installed_extensions`, `manage_skill`, `get_skills` |
| Theme registry | P05 C8 | `list_themes`, `set_theme`, `get_theme` (server-side `ThemeRegistry`, theme-kind `ui_request` interception) |
| MCP status | P27 C2 | `mcp_status` |
| Plan-mode RPC | P14 C5 | `set_plan_mode`, `get_plan_mode` |
| Observability | P25 C5–C7 | `get_metrics`, `get_session_stats` (server `TelemetryMetricsAggregator`) |
| Continuity | P23 | `set_goal`, `get_goal`, `schedule_job`, `list_jobs`, `cancel_job`, `autonomous`, `get_continuity_state` |
| Advisor lane | P16 | `advisor_note` mapped into the per-session event stream (`AgentSessionEvent.FromAdvisor`) |

## Plugin test suites

Each plugin ships a matching `tests/` project (xunit 2.9.2, .NET SDK 17.12.0, runner 2.8.2,
`IsPackable=false`). Run a single suite with:

```powershell
dotnet test tests/PiSharp.Memory.Tests
```

See [Implementation status](pisharp-implementation-status.md) for per-plugin test counts and
evidence.
