# PiSharp Current-State Catalog

Evidence-driven catalog of the current (main-branch) PiSharp codebase, produced 2026-08-14 to feed the gap analysis and plugin-planning exercise. Every claim cites a source file path and, where useful, a symbol. Anything not directly observed is marked `[INFERENCE]`. The `G:\code\AI\pi\PiSharp\.worktrees\daemon-client-architecture\` worktree contents are treated as in-flight and covered separately in [§11](#11-in-flight-work-daemon-client-architecture-not-in-main).

- Repo root: `G:\code\AI\pi\PiSharp` (git worktree, branch `main`, clean at inspection time — `git status --porcelain` returned nothing; `git branch --show-current` → `main`).
- Target framework: `net10.0` (`G:\code\AI\pi\PiSharp\src\PiSharp.Cli\PiSharp.Cli.csproj` → `<TargetFramework>net10.0</TargetFramework>`).
- Solution: `G:\code\AI\pi\PiSharp\PiSharp.sln`.

---

## 1. Project layout

`G:\code\AI\pi\PiSharp\Directory.Build.props` sets `VersionPrefix 0.0.0`, `VersionSuffix dev`, and a stable `AssemblyVersion 1.0.0.0` with the comment *"Keep assembly identity stable so installed native extensions can bind across package builds."* Authors/Copyright: Jimmeh; `RepositoryUrl https://github.com/imjimmeh/PiSharpio`; `PackageLicenseExpression MIT`.

### 1.1 `src\` projects

| Project | Role (verified against source) |
| --- | --- |
| `src\PiSharp.Abstractions` | Cross-cutting abstractions: `IExecutionEnv`/`IFileSystem`/`IShell` (Environment\), sessions (`ISession<TMetadata>`, `ISessionStorage<TMetadata>`, `SessionTreeEntry` hierarchy in `Sessions\SessionTreeEntry.cs`), messages (`AgentMessage`, `MessageContent` variants), options (`ThinkingLevel` in `Options\ThinkingLevel.cs`), streaming, errors, `Result.cs`. |
| `src\PiSharp.Agent.Core` | Contract-only agent layer: `IAgentTool` (`Tools\AgentToolContracts.cs`), `AgentEvent` 10-variant union (`Events\AgentEvent.cs`), `AgentHarnessEvent`/`AgentHarnessOwnEvent` (`Events\AgentHarnessEvent.cs`), `ModelDescriptor` (`Models\ModelDescriptor.cs`), `AgentLoopConfig` (`Loops\AgentLoopConfig.cs`), prompt contracts (`Prompting\PromptInterfaces.cs`, `SystemPromptDocument.cs`), `AssistantMessageEvent` (Streaming\). |
| `src\PiSharp.Agent` | Agent implementation: `AgentHarness<TMetadata>` (`Harness\AgentHarness.cs`, 34 KB), 5-stage loop pipeline (`Harness\LoopEvents\`), `AgentLoop` (`Loops\AgentLoop.cs`), `ToolCallExecutor` (`Loops\ToolCallExecutor.cs`), JSONL session storage (`Sessions\JsonlSessionStorage.cs`, `JsonlSessionRepo.cs`), `Session<TMetadata>` (`Sessions\Session.cs`), compaction (`Compaction\CompactionService.cs`, `BranchSummarizationService.cs`), skills (`Resources\SkillManager.cs`), prompt templates (`Resources\PromptTemplateCatalog.cs`), JSON serializers (`Serialization\AgentJsonSerializer.cs` + converters). |
| `src\PiSharp.Ai` | Provider abstraction (`Providers\IModelProvider.cs`), 11 built-in providers (`Providers\BuiltInProviders.cs`), `ApiRegistry` (`Registry\ApiRegistry.cs`), `ModelRegistry` + generated catalog (`Models\ModelRegistry.cs`, `Models\Generated\BuiltInModels.g.cs`), `ModelsJsonCatalogLoader`, credential resolution + OAuth (`Auth\ProviderCredentialResolver.cs`, `EnvApiKeyDetector.cs`, `FileOAuthStorage.cs`, `OAuthProviderRegistry.cs`, `OAuthHttpServer.cs`), extension surface (`PublicApi.cs`). |
| `src\PiSharp.Ai.ModelGenerator` | Build-time model catalog generator: `Program.cs` (2 lines) runs `ModelCatalogGenerator.Main(args)` (`G:\code\AI\pi\PiSharp\src\PiSharp.Ai.ModelGenerator\Program.cs`). |
| `src\PiSharp.Tools` | Built-in tools (`BuiltInTools.cs`), each tool under `Files\` (ReadTool, WriteTool), `Edit\` (EditTool), `Bash\` (BashTool), `Search\` (GrepTool, FindTool, LsTool), plus `Shared\` helpers (PathUtilities, FileMutationQueue, Truncation, OutputAccumulator, ImageUtilities) and `ToolSchemas.cs`/`JsonTool.cs`. |
| `src\PiSharp.Extensions` | Native extension API: `IExtension`, `IExtensionApi`, `ExtensionRegistry`, `ExtensionManager`, `ExtensionRuntimeBinding`, `ExtensionEvents`, `ExtensionUi`, `ExtensionParityContracts`, `ExtensionToolRegistration`, `ExtensionChatRows`, `PromptDocumentPatches`, `ExtensionRegistryChangeStream`, `ExtensionEventBus`, `ExtensionDescriptor`, `ExtensionMetadataAttribute`, `ExtensionSkillRegistration`. |
| `src\PiSharp.PluginHost` | Native `.dll` plugin loader: `NativePluginHost` (`PluginHost.cs`), collectible `PluginLoadContext : AssemblyLoadContext(isCollectible: true)` (`PluginLoadContext.cs`), `PluginHostOptions` (`PluginHostOptions.cs`). |
| `src\PiSharp.TsBridge` | Node sidecar bridge: `TsExtensionHost` (`TsExtensionHost.cs`, 90 KB), `TsBridgeManifestFactory` (C# parity manifest), `Protocol\TsBridgeManifestContracts.cs`, `TsDescriptorCache`, `JsonRpc\`, and `Node\` (TypeScript runner: `Node\TsBridgeRunner.mjs` → `src/runnerMain.ts`, `src/runner\piApi.ts`, `src/runner\uiApi.ts`, `src/shims\*`). |
| `src\PiSharp.Compatibility` | Pi-compatible settings/sessions/resources: `Settings\PiSettingsStore`/`PiAgentPaths`, `Resources\PiResourceLoader`, `Sessions\` compatibility behaviors. |
| `src\PiSharp.Coordination` | Native extension for multi-agent coordination: `CoordinationExtension.cs`, `CoordinationDaemon.cs` (named-pipe daemon), `CoordinationClient.cs`, `CoordinationJsonlStore.cs`, `CoordinationDaemonConnector.cs`, `SoftConflictDetector.cs`, `FileToolActivityParser.cs`, `PiSubagentsEventAdapter.cs`. |
| `src\PiSharp.Coordination.Daemon` | Standalone daemon executable: `Program.cs` (`--repo-root <path> --pipe-name <name>`), hosts `CoordinationDaemon` under `<repo>/.pi/coordination`. |
| `src\PiSharp.Runtime` | Bootstrap + wiring: `Runtime\PiRuntimeBootstrap.cs` (`CreateRuntimeAsync`), `Runtime\SessionRuntime.cs`, `Runtime\RuntimeExtensionBinder.cs`, `Runtime\RuntimeModelSelector.cs`, `Subagents\SubagentSessionService.cs` + `SubagentSessionHandle.cs` + `JsPiSubagentEventTranslator.cs`, `IO\`. |
| `src\PiSharp.Cli` | Entry point + CLI: `Program.cs`, `Parsing\CliParser.cs`/`CliArgs.cs`, `Modes\` (InteractiveMode, PrintMode, RpcMode, SubagentJsonMode), `Commands\` (slash commands), `Packages\` (package manager), `Logging\` (file logging), `Bootstrap\CliRuntimeOptionsMapper.cs`. |
| `src\PiSharp.Tui` | Terminal UI: `Interactive\TuiHost.cs`, `TuiHostOptions.cs`, `TuiRenderState.cs` (28 KB, `Reduce`), `ExtensionUiBridgeHost.cs` (28 KB), `TuiStateGateway.cs`, plus `Prompt\`, `Input\`, `Components\`, `Harness\`, `Shell\`, `Theme\` subdirectories. Built on Terminal.Gui (`docs\terminal-gui-architecture-reference.md`). |
| `src\PiSharp.Server` | ASP.NET Core host: `Program.cs` (`/health`, `/ws`), `WebSockets\PiServerWebSocketHandler.cs`, `Runtime\ServerSessionRegistry.cs` + `LiveServerSession.cs`, `Authentication\ApiKeyValidator.cs`, `Contracts\ServerContracts.cs`, `Serialization\ServerJsonSerializer.cs`. |
| `src\pisharp-session-webapp` | Frontend web app for the session server (Vite + TS: `vite.config.ts`, `index.html`). `[INFERENCE]` — no role beyond what the filename and files imply; not covered by any doc read. |

### 1.2 `tests\` projects

`tests\PiSharp.Agent.Tests`, `tests\PiSharp.Ai.Tests`, `tests\PiSharp.Cli.Tests`, `tests\PiSharp.Compatibility.Tests`, `tests\PiSharp.Coordination.Tests`, `tests\PiSharp.Extensions.Tests`, `tests\PiSharp.Extensions.Testing` (test helpers: `FakeExtensionApi`, `ExtensionTestFixture`), `tests\PiSharp.PluginHost.Tests`, `tests\PiSharp.Runtime.Tests`, `tests\PiSharp.Server.Tests`, `tests\PiSharp.TsBridge.Tests` (incl. `TsBridgeManifestTests`, `TsBridgeParityTests`, `WorkflowSessionsExtensionTests`), `tests\PiSharp.Tui.Tests` (incl. `TuiHostIntegrationTests`, `TuiRenderingTests`, `ExtensionUiBridgeHostTests`). No `PiSharp.Client.Tests` exists on main (see §11).

### 1.3 `extensions\` (shipped TypeScript extensions)

- `extensions\workflow-sessions` — workflow/DAG orchestration (`src\index.ts`, `README.md`).
- `extensions\pisharp-embeddings` — embeddings extension service (`src\index.ts`).
- `extensions\relevance-filtered-skills` — skill ranking/prompt-section filtering (`src\index.ts`).

See §7.4.

### 1.4 Other top-level items

- `javascript\` — the TypeScript reference implementation. Per `docs\analysis\ANALYSIS-epic-12-js-extension-parity.md` (line 5): *"`javascript/packages/coding-agent/` is reference-only and must not be modified."*
- `docs\plans\` and `docs\analysis\` — 70+ design/implementation plan docs and analysis docs (filenames scanned; only the ones cited below were read). Design-doc coverage areas include: daemon-client architecture (`2026-08-14-daemon-client-architecture*.md`), skills/extensions loading (`2026-05-26-skills-extensions-loading.md`), full-multiturn subagents (`2026-06-02-full-multiturn-subagents*.md`), JS extension parity (Epic-12, `2026-06-01-complete-js-pi-extension-api-parity.md`, `2026-06-01-epic-12-js-extension-parity-*.md`), structured logging (`2026-06-01-structured-logging-*.md`), TUI refactors, slash commands, OAuth, model command, session-scoped logs, native agent coordination (`2026-06-04-native-agent-coordination*.md`), TS bridge refactors, custom UI bridge, system prompt composer/parity.

---

## 2. Agent runtime

### 2.1 Bootstrap

`PiRuntimeBootstrap.CreateRuntimeAsync()` (`G:\code\AI\pi\PiSharp\src\PiSharp.Runtime\Runtime\PiRuntimeBootstrap.cs`) builds a `SessionRuntime`. The docs enumerate the sequence (`docs\pisharp-runtime.md` §Startup sequence, verified in source): layered settings (`PiSettingsStore`) → built-in providers + `models.json` → `PiResourceLoader` resources → prompt templates/theme → JSONL session → built-in tools/active-tool selection → native `.dll` extensions → TypeScript bridge (if TS extension paths exist) → extension CLI flags → `resources_discover` → provider/model/thinking selection → system-prompt options → `AgentHarness<TMetadata>` → `session_start` event. `SessionRuntime` (`src\PiSharp.Runtime\Runtime\SessionRuntime.cs`) owns session repo/current session, harness, extension manager, plugin host, TS host, settings snapshot, model selection, resources, skills, prompt templates, theme, startup diagnostics. Runtime snapshots are cached in `RuntimeExtensionBinder` and invalidated by cheap keys (session leaf, model, tool set, thinking level, extension registry).

### 2.2 Session lifecycle and the harness

`AgentHarness<TMetadata>` (`G:\code\AI\pi\PiSharp\src\PiSharp.Agent\Harness\AgentHarness.cs`) exposes (public surface verified by grep): `Session`, `Phase`, `Model`, `LastPromptDocument`, `ThinkingLevel`, `AllToolNames`, `ActiveToolNames`, `Skills`/`AllSkillNames`/`SelectedSkillNames`, `Subscribe(Func<AgentHarnessEvent, CancellationToken, Task>)`, `Steer`, `FollowUp`, `QueueNextTurn`, `DispatchSessionStartAsync`, `Abort`, `SetActiveTools`, `SetSelectedSkills`, `WaitForIdleAsync`, `RegisterTool`/`UnregisterTool`/`UnregisterToolsBySource`, `RegisterSkill`/`UnregisterSkill`/`UnregisterSkillsBySource`, `SetModelAsync`, `SetThinkingLevelAsync`, `SetSessionNameAsync`, `PromptAsync(text, images?)`, `CompactAsync(customInstructions?)`, `NavigateTreeAsync(targetId, summarize?)`.

Phase model: `public enum AgentHarnessPhase { Idle, Turn, Compaction, BranchSummary }` (`AgentHarness.cs`, line 666). `PromptAsync` throws if not `Idle`; `CompactAsync`/`NavigateTreeAsync` likewise (`"Harness is busy"` / `"compact() requires idle harness"` / `"navigateTree() requires idle harness"`). `AgentLoop` (`src\PiSharp.Agent\Loops\AgentLoop.cs`, 21 KB) runs turns; `ToolCallExecutor` (`Loops\ToolCallExecutor.cs`) executes tool calls.

Loop event processing is a durability-first pipeline (`docs\pisharp-developer-guide.md` §Agent harness and event flow, verified): `src\PiSharp.Agent\Harness\LoopEvents\` contains `PersistenceStage.cs` (queues message writes, flushes on TurnEnd/AgentEnd), `PhaseTransitionStage.cs` (resets phase to Idle on AgentEnd), `ToolMiddlewareStage.cs` (before/after tool middleware), `ExtensionDispatchStage.cs` (dispatches extension-visible events, isolates handler failures), `ListenerNotificationStage.cs` (parallel listener notification). Ordering keeps session writes ahead of extension/UI notification.

### 2.3 Prompt pipeline

The system prompt is a structured document: `SystemPromptDocument` + `PromptSection` (`src\PiSharp.Agent.Core\Prompting\SystemPromptDocument.cs`), composed by `ISystemPromptComposer` with `IPromptContributor`/`IPromptTransform`/`IPromptRenderer` (`Prompting\PromptInterfaces.cs`). Composition context: `SystemPromptCompositionContext` (`Prompting\SystemPromptCompositionContext.cs`). `AgentLoop` builds per-turn prompts; the harness publishes `BeforePromptRender` and `BeforeAgentStart` owned events before each turn (see §8). Extensions can patch sections via `PromptDocumentPatch` (see §7).

### 2.4 Tool execution

`IAgentTool`/`AgentToolResult<TDetails>` in `src\PiSharp.Agent.Core\Tools\AgentToolContracts.cs` (full interface quoted in §3). `ToolExecutionMode { Sequential, Parallel }`; tools can stream progress via `AgentToolUpdateCallback<TDetails>`; the harness publishes `ToolCall`/`ToolResult` (core `AgentEvent`) and `ToolExecutionStart/Update/End` (core `AgentEvent` variants, `src\PiSharp.Agent.Core\Events\AgentEvent.cs`).

### 2.5 Compaction

`CompactionService` (`src\PiSharp.Agent\Compaction\CompactionService.cs`): `CompactionSettings Default = new(true, 16384, 20000)` (enabled, reserve tokens 16,384, trigger when context > window − reserve); `EstimateTokens(AgentMessage)` (chars/4 heuristic, image = 4,800 tokens); `ShouldCompact(contextTokens, contextWindow, settings)`; `FindCutPoint(entries, keepRecentTokens)` (splits mid-turn when the cut lands inside a turn — `isSplitTurn`/`FindTurnStart`); `Prepare(path, settings)`. Compaction runs through the harness (`CompactAsync`) which sets phase `Compaction`, calls the completion delegate, and appends a `CompactionEntry` (see §2.6). `BranchSummarizationService` (`Compaction\BranchSummarizationService.cs`) collects from an old leaf to the common ancestor and generates a `BranchSummaryEntry` when navigating the tree (`NavigateTreeAsync(targetId, summarize: true)` → phase `BranchSummary`).

### 2.6 Session persistence format (JSONL)

- Files: `src\PiSharp.Agent\Sessions\JsonlSessionStorage.cs` (14 KB), `JsonlSessionRepo.cs`, `Session.cs`, `MemorySessionStorage.cs`; serializers in `src\PiSharp.Agent\Serialization\` (`AgentJsonSerializer`, `SessionTreeEntryJsonConverter`, `AgentMessageJsonConverter`, etc.).
- Format: **JSONL with a first header line**. `JsonlSessionStorage.CreateAsync` builds `new SessionHeader("session", 3, sessionId, DateTimeOffset.UtcNow, cwd, parentSessionPath)` (version 3; header line written first — `JsonlSessionStorage.cs:39-41`; `OpenAsync` requires `header.Type == "session"` and `Version is > 0 and <= 3`). Each subsequent line is one serialized `SessionTreeEntry`. The file is written lazily: `CreateAsync(...)` only persists immediately when `persistImmediately` is set; otherwise the header is written when the session materializes (runtime doc: *"deferred write until first user message"*).
- Entry types (`G:\code\AI\pi\PiSharp\src\PiSharp.Abstractions\Sessions\SessionTreeEntry.cs`, every entry carries `Id`, `ParentId`, `Timestamp`): `MessageEntry` (`"message"`), `ThinkingLevelChangeEntry` (`"thinking_level_change"`), `ModelChangeEntry` (`"model_change"` — Provider, ModelId), `CompactionEntry` (`"compaction"` — Summary, FirstKeptEntryId, TokensBefore, Details?, FromHook?), `BranchSummaryEntry` (`"branch_summary"` — FromId, Summary), `CustomEntry` (`"custom"` — CustomType, Data), `CustomMessageEntry` (`"custom_message"` — CustomType, Content, Display, Details), `LabelEntry` (`"label"` — TargetId, Label), `SessionInfoEntry` (`"session_info"` — Name), `LeafEntry` (`"leaf"` — TargetId).
- Compatibility: `JsonlSessionRepo` runs with `writeLeafEntries: false` in Pi-compat mode (default); `--no-compatibility` writes `LeafEntry` records (`docs\pisharp-runtime.md` §Sessions; `JsonlSessionStorage.OpenAsync` filters `LeafEntry` when `writeLeafEntries` is false, using the last `LeafEntry.TargetId` as legacy leaf).
- Branching/fork: entries form a parent-id tree; `Session.MoveToAsync(entryId, summary?)` moves the leaf and optionally appends a `BranchSummaryEntry`; `Session.BuildContextAsync` replays the branch to the leaf, rewriting compaction/branch-summary entries into `CompactionSummaryMessage`/`BranchSummaryMessage` (`Session.cs:103-111`).
- Sessions root: `~/.pi/agent/sessions` by default, per-cwd subdirectory `--<encoded-cwd>--` (`docs\pisharp-runtime.md` §Important paths; `PiAgentPaths.FromCwd` in `src\PiSharp.Compatibility\Settings\PiAgentPaths.cs`). Precedence: `--session-dir` → runtime `SessionsRoot` → `sessionDir` setting → default.
- Startup session options (CLI): new session, `--continue`/`-c` (latest in cwd), `--resume`/`-r` (CLI-level alias), `--session <id-or-path>`, `--fork <id-or-path>`, `--no-session` (in-memory), `--session-dir`. Interactive mode additionally runs `StartupResumeSelector` with a `SessionSelectorDialog` before runtime creation (`src\PiSharp.Cli\Program.cs:58-68`).

### 2.7 Thinking levels and model switching

- `ThinkingLevel` enum: `Off, Minimal, Low, Medium, High, XHigh` (`G:\code\AI\pi\PiSharp\src\PiSharp.Abstractions\Options\ThinkingLevel.cs`).
- Per-model thinking budget map: `ModelDescriptor.ThinkingLevelMap` (e.g. `claude-haiku-4-5`: `minimal=1024, low=1024, medium=4096, high=16384, xhigh=32000` — `src\PiSharp.Ai\Models\Generated\BuiltInModels.g.cs`; identical map across reasoning models).
- Switching: `AgentHarness.SetModelAsync`/`SetThinkingLevelAsync` (publishes `ModelSelect`/`ThinkingLevelSelect` owned events, appends `ModelChangeEntry`/`ThinkingLevelChangeEntry`); CLI `--model`/`--provider`/`--thinking`; `/model` slash command; RPC `set_model`/`set_thinking_level`; extension `api.Model.SetModelAsync`/`SetThinkingLevelAsync`. Resolution logic in `RuntimeModelSelector` (`src\PiSharp.Runtime\Runtime\RuntimeModelSelector.cs`): splits `provider/model`, parses `:thinking` suffix, applies scoped-model patterns, falls back to first catalog model. Model changes are persisted into the session stream, so resuming a session rebuilds provider/model/thinking from entries (`Session.BuildSessionContext`, `Session.cs:103-111`).

### 2.8 Harness options and loop configuration

`AgentHarnessOptions<TMetadata>` (verbatim fields, `G:\code\AI\pi\PiSharp\src\PiSharp.Agent\Harness\AgentHarnessOptions.cs`): `Session` (`ISession<TMetadata>`), `Model`, `StreamAsync` (`AgentStreamAsync`), `CompletionAsync` (`AgentCompletionAsync`), `Tools` (`IReadOnlyList<IAgentTool>`), `ActiveToolNames?`, `SystemPrompt?` (`SystemPromptBuildOptions`), `SystemPromptContext?`, `SystemPromptComposer?` / `SystemPromptComposerFactory?`, `Skills?` (`Skill[]`), `PromptTemplates?` (`PromptTemplate[]`), `ThinkingLevel` (default `Off`), `ToolExecution` (default `ToolExecutionMode.Parallel`), `StreamOptions?`, `Extensions?` (`ExtensionRegistry`).

`AgentLoopConfig` (verbatim, `G:\code\AI\pi\PiSharp\src\PiSharp.Agent.Core\Loops\AgentLoopConfig.cs`): `Model`, `StreamAsync`, plus optional loop hooks — `TransformContext`, `ConvertToLlm`, `GetApiKey`, `BeforeToolCall` (`Func<BeforeToolCallContext, Task<BeforeToolCallResult?>>`), `AfterToolCall` (`Func<AfterToolCallContext, Task<AfterToolCallResult?>>`), `PrepareNextTurn`, `ShouldStopAfterTurn`, `GetSteeringMessages`, `GetFollowUpMessages`, `ToolExecution` (default `Parallel`), `StreamOptions?`, `ThinkingLevel`. Supporting context records in the same file: `BeforeToolCallContext(AssistantMessage, ToolCall, Args, Context)`, `AfterToolCallContext(AssistantMessage, ToolCall, Args, Result, IsError, Context)`, `PrepareNextTurnContext`, `ShouldStopAfterTurnContext`, `AgentLoopTurnUpdate(Context?, Model?, ThinkingLevel?)`. These are the loop-level extension points the harness wires to middleware and extension dispatch (`src\PiSharp.Agent\Harness\LoopEvents\ToolMiddlewareStage.cs`).

The loop consumes steering/follow-up/next-turn queues (`AgentHarness.Steer`/`FollowUp`/`QueueNextTurn`) and drives tool execution through `ToolCallExecutor` (`src\PiSharp.Agent\Loops\ToolCallExecutor.cs`, 7.9 KB). Streaming deltas flow as `AssistantMessageEvent` (`src\PiSharp.Agent.Core\Streaming\AssistantMessageEvent.cs`) with delegates in `Streaming\AgentStreamDelegates.cs`.

---

## 3. Tools

### 3.1 Tool contract

`IAgentTool` (verbatim, `G:\code\AI\pi\PiSharp\src\PiSharp.Agent.Core\Tools\AgentToolContracts.cs`):

```csharp
public interface IAgentTool
{
    string Name { get; }
    string Label { get; }
    string Description { get; }
    string? PromptSnippet => null;
    IReadOnlyList<string> PromptGuidelines => [];
    JsonElement ParametersSchema { get; }
    ToolExecutionMode? ExecutionMode { get; }
    JsonElement PrepareArguments(JsonElement args);
    Task<AgentToolResult<object?>> ExecuteAsync(string toolCallId, JsonElement parameters,
        CancellationToken cancellationToken = default, AgentToolUpdateCallback<object?>? onUpdate = null);
}
```

Also in that file: `IAgentTool<TParameters, TDetails>` (typed variant), `IAgentToolRenderer` (`HasRenderCall`/`HasRenderResult`/`RenderCallAsync`/`RenderResultAsync`, with `ToolRenderRequest`/`ToolRenderResult`), `AgentToolResult<TDetails>(Content, Details, Terminate)`, `BeforeToolCallResult(Block, Reason)`, `AfterToolCallResult(Content?, Details?, IsError?, Terminate?)`.

### 3.2 Built-in tools

`BuiltInTools` (`G:\code\AI\pi\PiSharp\src\PiSharp.Tools\BuiltInTools.cs`): `CreateAll(env)` registers `read`, `bash`, `edit`, `write`, `grep`, `find`, `ls` (one line each: `new ReadTool(env)`, `new BashTool(env)`, `new EditTool(env)`, `new WriteTool(env)`, `new GrepTool(env)`, `new FindTool(env)`, `new LsTool(env)`). `CreateReadOnly()` registers only `read`, `grep`, `find`, `ls`. `CreateTool(name, env)` maps name → instance. All are created with an `IExecutionEnv` (`PiSharp.Abstractions.Environment`); production env is `SystemExecutionEnv` (constructed in `src\PiSharp.Cli\Program.cs:56`).

| Tool | Class / file | Purpose | Subprocess/shell? |
| --- | --- | --- | --- |
| `read` | `ReadTool`, `src\PiSharp.Tools\Files\ReadTool.cs` | Read text and supported image files (image handling via `Shared\ImageUtilities.cs`). | No — filesystem only (`IFileSystem`). |
| `bash` | `BashTool`, `src\PiSharp.Tools\Bash\BashTool.cs` | Execute a shell command in the cwd; returns stdout/stderr, truncates to `Truncation.DefaultMaxLines`/`DefaultMaxBytes` with temp-file spill; optional `timeout` seconds; throttled partial updates (100 ms). | **Yes** — `_env.ExecAsync(command, execOptions, ct)` on `IExecutionEnv` (which wraps the `IShell` abstraction from `PiSharp.Abstractions`); typed input `BashToolInput`. |
| `edit` | `EditTool`, `src\PiSharp.Tools\Edit\EditTool.cs` (+ `EditDiff.cs`) | Exact-text replacement edits with diff preview. | No. |
| `write` | `WriteTool`, `src\PiSharp.Tools\Files\WriteTool.cs` | Create/overwrite files. | No. |
| `grep` | `GrepTool`, `src\PiSharp.Tools\Search\GrepTool.cs` | Search file contents (backends in `ContentSearchBackends.cs`/`FileSearchBackends.cs`). | No. |
| `find` | `FindTool`, `src\PiSharp.Tools\Search\FindTool.cs` | Find files by glob/pattern (`GlobMatcher.cs`). | No. |
| `ls` | `LsTool`, `src\PiSharp.Tools\Search\LsTool.cs` | List directory contents. | No. |

Shared helpers in `src\PiSharp.Tools\Shared\`: `PathUtilities` (path resolution relative to execution env/cwd), `FileMutationQueue` (serializes file mutations), `Truncation` + `OutputAccumulator` (bounded output, truncation metadata, temp spill), `ImageUtilities`. Schemas via `ToolSchemas` (`src\PiSharp.Tools\ToolSchemas.cs`) / `JsonTool<TParameters,TDetails>` (`JsonTool.cs`).

### 3.3 Tool registration and selection

- Built-ins are registered into the harness by the runtime (Bootstrap step "creates built-in tools and selects active tools"). Selection controls: `--tools`/`-t <a,b>` (restrict active tools), `--no-tools`/`-nt`, `--no-builtin-tools`/`-nbt` (extensions can still register tools); runtime `SetActiveTools`.
- Extension tools: `ExtensionToolRegistration` (`src\PiSharp.Extensions\ExtensionToolRegistration.cs`) wraps a delegate into `ExtensionRegisteredTool : IAgentTool` via `ToAgentTool()`; registered through `IExtensionApi.RegisterTool` / `IExtensionApi.Tools.RegisterTool`, stored in `ExtensionRegistry._tools` (key `tool:{name}`). Duplicate names rejected unless `ExtensionOverridePolicy.Override` / `OverrideBuiltIn` (`docs\pisharp-tools.md` §Registering extension tools; `src\PiSharp.Extensions\ExtensionParityContracts.cs` enum).
- Tool events visible to extensions: `tool_call`, `tool_result`, `tool_execution_start`, `tool_execution_update`, `tool_execution_end`; middleware can block (`context.Blocked`/`BlockReason`) or modify results (`ModifyToolResult`) — `src\PiSharp.Extensions\ExtensionEvents.cs` `ExtensionMiddlewareContext`.
- User shell requests: interactive `! <command>` / `!! <command>` and RPC `bash`/`abort_bash` route through the `user_bash` hook surface (`docs\pisharp-tools.md` §File and shell behavior; `src\PiSharp.Cli\Modes\RpcMode.cs` `case "bash"`/`case "abort_bash"`). `abort_bash` returns a no-active-operation response; *"until a long-lived bash runner is introduced"* (doc text).

### 3.4 Tool schemas and typed parameters

- Schemas: `ToolSchemas.FromType<T>()` generates JSON schemas from typed input records (`src\PiSharp.Tools\ToolSchemas.cs`); `JsonTool<TParameters, TDetails>` (`src\PiSharp.Tools\JsonTool.cs`) is the base for typed built-ins (e.g. `BashTool : JsonTool<BashToolInput, BashToolDetails?>`).
- `BashToolInput` (verbatim, `src\PiSharp.Tools\Bash\BashTool.cs:119-124`): `Command` (required, *"Bash command to execute"*) and `Timeout` (`double?`, *"Timeout in seconds (optional, no default timeout)"*). Details: `BashToolDetails(TruncationResult? Truncation, string? FullOutputPath)`. Options: `BashToolOptions(CommandPrefix?, Environment?, SpawnHook?)` — `BashSpawnHook` transforms the `BashSpawnContext(Command, Cwd, Environment)` before execution (used to inject prefixes/env per spawn; not part of the public tool contract).
- Truncation messaging (from `BashTool.FormatOutput`): on truncation the model-visible text appends `[Showing lines N-M of T (… limit). Full output: <path>]` and the full output is spilled to a temp file (`OutputAccumulator`, `Shared\OutputAccumulator.cs`; temp prefix `pi-bash`).
- Typed input records for the other six built-ins (verified at the file tails): `ReadToolInput(Path, Offset? int, Limit?)` (*"Line number to start reading from (1-indexed)"*, *"Maximum number of lines to read"*) + `ReadToolOptions(AutoResizeImages = true)`; `EditToolInput(Path, Edits: IReadOnlyList<EditReplacement>)` (*"Each edit is matched against the original file, not incrementally"*) + `EditToolDetails(Diff, FirstChangedLine)`; `WriteToolInput(Path, Content)`; `GrepToolInput(Pattern, Path?, Glob?, IgnoreCase?, Literal?, Context?, Limit?)`; `FindToolInput(Pattern, Path?, Limit? = 1000)` + `FindToolDetails(Truncation?, ResultLimitReached?)`; `LsToolInput(Path? = null, Limit? = 500)` + `LsToolDetails(Truncation?, EntryLimitReached?)` — all `JsonTool<TParameters, TDetails>` subclasses with `ToolSchemas.FromType<TInput>()`.

---

## 4. Providers / Models

### 4.1 Abstraction

`IModelProvider` (verbatim, `G:\code\AI\pi\PiSharp\src\PiSharp.Ai\Providers\IModelProvider.cs`): `string Api { get; }`, `IAsyncEnumerable<AssistantMessageEvent> StreamAsync(ModelDescriptor model, AgentContext context, AgentStreamOptions options, CancellationToken)`, `Task<AssistantMessage> CompleteAsync(...)`.

`ApiRegistry` (`src\PiSharp.Ai\Registry\ApiRegistry.cs`): `RegisteredApiProvider(Api, Provider, SourceId)`; `ConcurrentDictionary<string, RegisteredApiProvider>` keyed by api name (Ordinal); `Register` is last-write-wins; static `StreamAsync`/`CompleteAsync` resolve via `Resolve(model.Api)` and enforce `EnsureApiMatches` (throws if `model.Api != Api`).

### 4.2 Built-in providers

`BuiltInProviders.RegisterAll` (`src\PiSharp.Ai\Providers\BuiltInProviders.cs`, `SourceId = "built-in"`) registers 11 providers:

| Api name | Provider class |
| --- | --- |
| `anthropic-messages` | `AnthropicProvider` (`Providers\Anthropic\AnthropicProvider.cs`) |
| `openai-responses` | `OpenAIResponsesProvider` (`Providers\OpenAI\OpenAIResponsesProvider.cs`) |
| `openai-completions` | `OpenAICompletionsProvider` |
| `openai-chat-completions` | `OpenAICompletionsProvider(api: "openai-chat-completions")` |
| `azure-openai-responses` | `OpenAIResponsesProvider(api: "azure-openai-responses")` |
| `openai-codex-responses` | `OpenAIResponsesProvider(api: "openai-codex-responses")` |
| `google-generative-ai` | `GoogleProvider` (`Providers\Google\GoogleProvider.cs`) |
| `google-vertex` | `GoogleVertexProvider` (`Providers\Google\GoogleVertexProvider.cs`) |
| `bedrock-converse-stream` | `BedrockProvider` (`Providers\Bedrock\BedrockProvider.cs`) |
| `mistral-conversations` | `MistralProvider` (`Providers\Mistral\MistralProvider.cs`) |
| `faux` | `FauxProvider` (`Providers\Faux\FauxProvider.cs`) — scripted mock (text/thinking/tool/error/abort items; `DefaultApi = "faux"`) |

Provider/HTTP/SSE plumbing: `Providers\Shared\ProviderHttp.cs`, `MessageTransformer.cs`, `ToolTransformer.cs`, `StopReasonMapper.cs`, `ToolCallDeltaAssembler.cs`, plus per-provider stream parsers and request mappers.

### 4.3 Model catalog

- `ModelDescriptor` (`src\PiSharp.Agent.Core\Models\ModelDescriptor.cs`): `Provider, Id, Api, Name, BaseUrl, Reasoning, ContextWindow, MaxTokens, ThinkingLevelMap, Input` (modalities), `Cost` (`ModelCost(Input, Output, CacheRead, CacheWrite)` per 1M tokens), `Headers`, `Compat` (`AnthropicCompat(CacheControl)` | `OpenAICompat(Strict, MaxTokensField)`), `ApiKey`, `AuthHeader`.
- `ModelRegistry` (static, `src\PiSharp.Ai\Models\ModelRegistry.cs`): seeded in the static ctor from `BuiltInModels.All` (`src\PiSharp.Ai\Models\Generated\BuiltInModels.g.cs`, 12,882 lines, auto-generated; collection expression of `new("provider", "id", new ModelDescriptor(...))` grouped per provider). Sources: `ModelCatalogGenerator` (`Models\Generation\ModelCatalogGenerator.cs`) pulls `https://models.dev/api.json`, `https://openrouter.ai/api/v1/models`, `https://ai-gateway.vercel.sh/v1/models`, plus `ManualCodexModels()` (provider `openai-codex`, api `openai-codex-responses`, base `https://chatgpt.com/backend-api`). Run via `src\PiSharp.Ai.ModelGenerator\Program.cs` → `ModelCatalogGenerator.Main(args)`.
- Sample entries (verbatim from `BuiltInModels.g.cs`): `claude-sonnet-4-5` (anthropic / `anthropic-messages`, ContextWindow 200000, MaxTokens 64000, ThinkingLevelMap 5-level, Cost Input 3m/Output 15m); `claude-opus-4-5` (Input 5m/Output 25m); `claude-haiku-4-5` (Input 1m/Output 5m); `gemini-2.5-pro` (google / `google-generative-ai`, ContextWindow 1048576, MaxTokens 65536); `gemini-2.5-flash`; `gpt-5` (openai / `openai-responses`, ContextWindow 400000, MaxTokens 128000, Reasoning true); `gpt-5.2-codex`; `gpt-4o-mini` (Reasoning false); `mistral-large-latest` (mistral / `mistral-conversations`, ContextWindow 262144); `amazon.nova-lite-v1:0` (amazon-bedrock / `bedrock-converse-stream`, ContextWindow 300000).
- `models.json` override: `ModelsJsonCatalogLoader` (`src\PiSharp.Ai\Models\ModelsJsonCatalogLoader.cs`) parses `~/.pi/agent/models.json` (`PiAgentPaths.ModelsPath`, `src\PiSharp.Compatibility\Settings\PiAgentPaths.cs:37`) — root `providers` object; per provider either a `models` array (full replacement catalog) or a provider-level override (re-registers every existing built-in model with patched api/baseUrl/headers/apiKey/authHeader). Loaded after built-ins at runtime (`PiRuntimeBootstrap.EnsureProvidersRegistered`), so its registry `Order` wins.
- Registry precedence: `OwnedModel(value, SourceId, Order)`; resolution sorts by `Order` descending; `GetCustomProviders()` = providers with ≥1 non-built-in config; `IsProviderAccessible(provider, storedProviders, customProviders)` = custom → stored → `EnvApiKeyDetector.HasAmbientCredentials`. `CalculateCost(model, usage)`; `GetSupportedThinkingLevels`/`ClampThinkingLevel` use `ThinkingLevelMap`.

### 4.4 Credential resolution

`ProviderCredentialResolver` (`src\PiSharp.Ai\Auth\ProviderCredentialResolver.cs`) resolves per request: (1) `options.ApiKey`; (2) OAuth token (walking `ProviderCandidates(provider)` incl. aliases `openai-codex → [openai]`); (3) configured key (`model.ApiKey ?? providerConfig.ApiKey` — an env-var-style name reads the env var; `"<authenticated>"` marker nulls the key); (4) `EnvApiKeyDetector.GetEnvApiKey(provider)`. Header merge (later wins): ambient → `EnvApiKeyDetector.GetProviderHeaders` → `providerConfig.Headers` → `model.Headers` → `options.Headers`; `Authorization: Bearer` set when `useAuthHeader` (default true).

`EnvApiKeyDetector.ProviderEnvVarMap` (exact env vars, `src\PiSharp.Ai\Auth\EnvApiKeyDetector.cs`):

| Provider | Env vars |
| --- | --- |
| `anthropic` | `ANTHROPIC_OAUTH_TOKEN`, `ANTHROPIC_API_KEY` |
| `openai` | `OPENAI_API_KEY` |
| `azure-openai-responses` | `AZURE_OPENAI_API_KEY` |
| `google` | `GEMINI_API_KEY` |
| `mistral` | `MISTRAL_API_KEY` |
| `openrouter`, `together`, `fireworks`, `groq`, `xai`, `deepseek`, `cerebras`, `moonshot`, `kimi` | `<PROVIDER>_API_KEY` variants |
| `google-vertex` | no key; ambient only — `GOOGLE_APPLICATION_CREDENTIALS` (file must exist) + `GOOGLE_CLOUD_PROJECT` + `GOOGLE_CLOUD_LOCATION` → `AuthenticatedMarker`; headers `x-goog-user-project`, `x-goog-location` |
| `amazon-bedrock` | no key; ambient only — `AWS_ACCESS_KEY_ID` + `AWS_SECRET_ACCESS_KEY` → `AuthenticatedMarker` |

### 4.5 OAuth

- Storage: `IOAuthStorage` (`Auth\OAuthStorage.cs`) + `FileOAuthStorage` (`Auth\FileOAuthStorage.cs`) at `~/.pi/agent/auth.json` (`PiAgentPaths.AuthPath`). Record shape: `{ "providers": { "<providerId>": { "token": "..." } } }`, and for OAuth credentials `{ "access", "refresh", "expires", ...extra }`; tolerant of legacy shapes (bare string / `token|access|accessToken|access_token|apiKey|api_key|key` keys, nesting under `oauth|oauthToken|credentials|auth`, root-level provider entries).
- Registry: `OAuthProviderRegistry` (`Auth\OAuthProviderRegistry.cs`); registered at startup by `PiRuntimeBootstrap.RegisterBuiltInOAuthProviders` (`PiRuntimeBootstrap.cs:526-530`):
  - `AnthropicOAuthProvider` (`Auth\AnthropicOAuthProvider.cs`) — Id `anthropic`, callback server port **53692**, scopes `org:create_api_key user:profile user:inference ...`, PKCE, manual redirect-URL paste fallback.
  - `GitHubCopilotOAuthProvider` (`Auth\GitHubCopilotOAuthProvider.cs`) — Id `github-copilot`, **device flow** (`UsesCallbackServer = false`), spoofed VS Code headers, token exchange + `EnableAllModelsAsync`.
  - `OpenAICodexOAuthProvider` (`Auth\OpenAICodexOAuthProvider.cs`) — Id `openai-codex`, callback port **1455**, redirect `http://localhost:1455/auth/callback`, scope `openid profile email offline_access`, `JwtClaimPath = "https://api.openai.com/auth"`, PKCE.
- `OAuthHttpServer` (`Auth\OAuthHttpServer.cs`): HttpListener-based; defaults random port 50000–59999, path `/callback`; `WaitForCodeAsync(timeout)`. Login entry points: `/login <provider>` slash command and `--login <provider>` (`src\PiSharp.Cli\Commands\BuiltIn\LoginSlashCommand.cs`, `Program.HandleLoginLogoutAsync`).

### 4.6 The `model` command and configured providers

- `/model` (and alias `/models`) — `src\PiSharp.Cli\Commands\BuiltIn\ModelSlashCommand.cs`: candidates = scoped models if `IsScoped`, else all `PublicApi.Models` filtered by `ModelRegistry.IsProviderAccessible(provider, storedSet, GetCustomProviders())` (fallback to all when filter empties; fallback to current model). No args → interactive `SelectAsync` (`provider/id — Name`); args → provider/id split, exact id → exact name → substring matches. Applies selection and clamps thinking level, then `Runtime.SetModelAsync(selection, "slash")` + persists selection.
- `/scoped-models` — `src\PiSharp.Cli\Commands\BuiltIn\ScopedModelsSlashCommand.cs`: lists `CurrentModelSelection.ScopedModels`.
- CLI mapping: `RuntimeModelOptions(Provider, Model, Thinking, ScopedModels)` (`src\PiSharp.Runtime\Runtime\PiRuntimeOptions.cs:26-27`); `RuntimeModelSelector.Resolve` (`src\PiSharp.Runtime\Runtime\RuntimeModelSelector.cs`) with `provider/model` and `model:ThinkingLevel` splitting, scoped patterns, `Cycle`/`CycleThinking`.
- "Configured provider" concept: defined in `docs\plans\2026-06-06-model-command-configured-providers-design.md` (login-stored credentials | models.json-defined | env-var key | ambient cloud creds). Implemented **in code only** — `ModelRegistry.GetCustomProviders()` + `IsProviderAccessible()` + `ModelSlashCommand` filter. **No `configuredProviders`/`ConfiguredProviders` setting key exists**: grep across `src\` returns zero matches.
- Extension provider registration surface: `PiSharp.Ai.PublicApi` (`src\PiSharp.Ai\PublicApi.cs`) — `RegisterBuiltInProviders`, `LoadModelsJson`, `RegisterProvider(IModelProvider, sourceId?)`, `UnregisterProviderSource`, `Stream/Complete(+Simple)`, `ResolveCatalogModel`.

---

## 5. CLI & modes

Entry: `src\PiSharp.Cli\Program.cs` (`Main` → `RunAsync`). `AppMode { Interactive, PrintText, PrintJson, Rpc, SubagentJson }`, `CliMode { Text, Json, Rpc, SubagentJson }` (`src\PiSharp.Cli\Parsing\CliArgs.cs`).

### 5.1 Mode selection (`CliParser.SelectAppMode`, `src\PiSharp.Cli\Parsing\CliParser.cs`)

1. `--mode rpc` → RPC.
2. `--mode json` → print JSON; with `-p --no-session` → **JavaScript Pi-compatible subagent JSONL mode** (same adapter as `subagent-json`).
3. `--mode subagent-json` → Pi-compatible subagent JSONL.
4. `--mode text` → print text.
5. `--print`/`-p` → print text.
6. Redirected stdin → print text.
7. Otherwise → interactive TUI.

### 5.2 CLI flags (CliArgs record, `src\PiSharp.Cli\Parsing\CliArgs.cs`; help text in `CliHelpRenderer`)

`--provider`, `--model`, `--api-key`, `--system-prompt`, `--append-system-prompt`, `--thinking`, `--continue`/`-c`, `--resume`/`-r`, `--help`/`-h`, `--version`/`-v`, `--mode`, `--no-session`, `--session`, `--fork`, `--session-dir`, `--models`, `--tools`/`-t`, `--no-tools`/`-nt`, `--no-builtin-tools`/`-nbt`, `--extension`/`-e`, `--no-extensions`/`-ne`, `--print`/`-p`, `--export`, `--import`, `--share`, `--login`, `--logout`, `--reload`, `--no-compatibility`, `--no-skills`, `--skill`, `--prompt-template`, `--no-prompt-templates`, `--theme`, `--no-themes`, `--no-context-files`/`-nc`, `--no-resources` (umbrella), `--list-models`, `--list-all-models`, `--offline`, `--verbose`, `--benchmark-startup`, positional messages/file args. Unknown long flags are captured (`UnknownFlags`) and claimed by extensions via `RegisterFlag` (`docs\pisharp-runtime.md` §Common CLI flags).

- **RPC mode** (`src\PiSharp.Cli\Modes\RpcMode.cs`, 18 KB): JSONL request/response loop on stdin/stdout. Complete verified command set (switch, lines 67-203): `prompt`, `steer`, `follow_up`, `abort`, `new_session`, `get_state`, `set_model`, `get_available_models`, `set_thinking_level`, `compact`, `fork`, `switch_session`, `get_messages`, `get_last_assistant_text`, `extension_ui_response`, `set_session_name`, `cycle_model`, `cycle_thinking_level`, `get_commands`, `run_command`, `clone`, `get_session_stats`, `get_fork_messages`, `set_auto_compaction`, `set_auto_retry`, `abort_retry`, `set_steering_mode` (`all`/`one-at-a-time`), `set_follow_up_mode` (`all`/`one-at-a-time`), `export_html`, `bash`, `abort_bash`. `get_commands` returns `BuildCommandRegistry(runtime).Commands.Where(c => c.SourceId != "builtin")` mapped by `ToRpcCommandInfo` — which synthesizes JS-pi-shaped `source` (`skill` → `skill`, `prompt-template` → `prompt`) and `sourceInfo { Path, Source, Scope = "temporary", Origin = "top-level" }` (`RpcMode.cs:218-237`); `run_command` executes through the same registry (`BuildCommandRegistry(runtime).ExecuteAsync`), and the registry builder is `SlashCommandRegistryFactory.Create(runtime)` — the same factory as interactive mode (built-ins + extension commands + `skill:<name>` + `prompt:<name>`). This supersedes the older note in `docs\analysis\pisharp-extension-api-inventory.md` that RPC omitted skill commands, and addresses the parity doc's command-shape mismatch except for hard-coded `scope`/`origin` values (see §12.1).

- **Interactive TUI** (`src\PiSharp.Cli\Modes\InteractiveMode.cs`, 12 KB): builds `TuiHostOptions` from `runtime.Harness` + delegate wiring; runs `TuiHost` (Terminal.Gui). Slash dispatch via `SlashCommandRegistryFactory.Create(runtime)`; user-bash `!`/`!!` input hooks; resume selector (`SessionSelectorDialog`). TUI rendering: `TuiRenderState.Reduce` + a bounded-channel `TuiHarnessEventPump` batching events (per `tests\PiSharp.Tui.Tests\TuiHostIntegrationTests.cs` coverage; `src\PiSharp.Tui\Interactive\Harness\`).
- **Print mode** (`src\PiSharp.Cli\Modes\PrintMode.cs`): one-shot prompt; `PrintOutputMode.Text | Json`; JSON output is the harness-event stream serialized with `AgentJsonSerializer` (`docs\pisharp-runtime.md` §CLI mode selection: `--mode json` keeps the native `AgentHarnessEvent` JSONL shape).
- **Subagent JSON mode** (`src\PiSharp.Cli\Modes\SubagentJsonMode.cs` + `SubagentJsonModeOptions.cs`): emits JavaScript Pi-compatible JSONL — a first `session` header line, then streamed lifecycle events (`AgentSessionEvent`), via `JsPiSubagentEventWriter`/`JsPiSubagentEventTranslator` (also used by the in-process subagent service, §10).
- **RPC contract records** (`src\PiSharp.Cli\Modes\RpcContracts.cs`): `RpcResponse(Id, Type="response", Command, Success, Data?, Error?)` with `Ok`/`Fail` factories; `RpcSessionState(Model, ThinkingLevel, IsStreaming, IsCompacting, SteeringMode, FollowUpMode, SessionFile?, SessionId, SessionName?, AutoCompactionEnabled, MessageCount, PendingMessageCount)` (returned by `get_state`); `RpcPromptCommand(Type, Id, Message, Images?, StreamingBehavior?)`; `RpcExtensionUiRequest(Id, Kind, Prompt, Options?)`; `RpcExtensionUiResponseCommand(Type, Id, RequestId, Value?, Confirmed?, Cancelled?)`; `RpcSetModelCommand`, `RpcSetThinkingLevelCommand`, `RpcSessionNameCommand`, `RpcEntryCommand`, `RpcNewSessionResult`, `RpcAvailableModelsResult(IReadOnlyList<ModelDescriptor>)`, `RpcMessagesResult`, `RpcLastAssistantTextResult`. RPC mode also runs an **extension UI round-trip**: `extension_ui_response` resolves a pending `TaskCompletionSource<RpcExtensionUiResponseCommand>` from `RpcMode.PendingExtensionUi` (`RpcMode.cs:18,125-129`) — the pattern the daemon design generalizes into `ui_request`/`ui_response` (§6.2).
- **RPC mode** (`src\PiSharp.Cli\Modes\RpcMode.cs`, 18 KB): JSONL request/response loop on stdin/stdout (`RpcContracts.cs` defines `RpcPromptCommand`, `RpcResponse`, `RpcAvailableModelsResult`, `RpcMessagesResult`, `RpcLastAssistantTextResult`, `RpcCommandInfo`). Commands handled (verified in the switch, lines ~67-204): `prompt`, `steer`, `follow_up`, `abort`, `new_session`, `get_state`, `set_model`, `get_available_models`, `set_thinking_level`, `compact`, `fork`, `switch_session`, `get_messages`, `get_last_assistant_text`, `set_session_name`, `cycle_thinking_level`, `get_commands`, `run_command`, `get_fork_messages`, `set_auto_compaction`, `bash`, `abort_bash` (plus `clone` case seen at line 152). `get_commands`/`run_command` use `SlashCommandRegistryFactory.Create(runtime)` — **the same factory as interactive mode**, which registers built-ins + extension commands + `skill:<name>` + `prompt:<name>` commands (`src\PiSharp.Cli\Commands\SlashCommandRegistryFactory.cs`). This supersedes the older note in `docs\analysis\pisharp-extension-api-inventory.md` that RPC omitted skill commands.
- **Package commands** (pre-runtime): `install`, `remove`/`uninstall`, `update` (`--extension`/`--extensions`/`self`), `list`, `config` (`src\PiSharp.Cli\Packages\PiPackageCommandRunner.cs`, `PiPackageSourceParser.cs`; npm/git/local sources; managed root `~/.pi/agent/packages`; `--local`, `--force`, `--offline`). Self-update is parsed but reports *"self-update is not implemented"* (`docs\pisharp-tools.md` §Package CLI; `EPIC-12` analysis notes object-form package filters are not implemented).

### 5.4 Slash commands

Built-in catalog (`src\PiSharp.Cli\Commands\BuiltInSlashCommandCatalog.cs`): 19 command classes, 22 names (aliases in parens): `settings`, `model`/`models`, `scoped-models`, `export`, `import`, `share`, `copy` (CopyLastAssistantMessage), `name`, `session`/`resume` (ResumeSession), `changelog`, `hotkeys`, `fork`/`clone` (ForkSession), `tree` (SessionTree), `login`, `logout`, `new` (NewSession), `compact`, `reload`, `quit`. Each class implements `IBuiltInSlashCommand` (`Commands\IBuiltInSlashCommand.cs` — `Names`, `Description`, `ExecuteAsync(context, args, ct)` with `SlashCommandContext` in `Commands\SlashCommandContext.cs`). Registry: `SlashCommandRegistry` (`Commands\SlashCommandRegistry.cs`) + `FuzzyMatcher` for prefix matching. `SlashCommandRegistryFactory.Create(runtime)` additionally registers extension commands (`registry.RegisterExtensions(runtime.ExtensionManager.Registry.Commands)`), `skill:<name>` commands (`sourceId "skill"`), and `prompt:<name>` commands (`sourceId "prompt-template"`) (`src\PiSharp.Cli\Commands\SlashCommandRegistryFactory.cs`).

Full class → name mapping (verified, `BuiltInSlashCommandCatalog.cs:9-27` and `Names` array lines 30-54):

| Class (`src\PiSharp.Cli\Commands\BuiltIn\`) | Names (22 total) |
| --- | --- |
| `SettingsSlashCommand` | `settings` |
| `ModelSlashCommand` | `model`, `models` |
| `ScopedModelsSlashCommand` | `scoped-models` |
| `ExportSessionSlashCommand` | `export` |
| `ImportSessionSlashCommand` | `import` |
| `ShareSessionSlashCommand` | `share` |
| `CopyLastAssistantMessageSlashCommand` | `copy` |
| `NameSlashCommand` | `name` |
| `ResumeSessionSlashCommand` | `session`, `resume` |
| `ChangelogSlashCommand` | `changelog` |
| `HotkeysSlashCommand` | `hotkeys` |
| `ForkSessionSlashCommand` | `fork`, `clone` |
| `SessionTreeSlashCommand` | `tree` |
| `LoginSlashCommand` | `login` |
| `LogoutSlashCommand` | `logout` |
| `NewSessionSlashCommand` | `new` |
| `CompactSlashCommand` | `compact` |
| `ReloadSlashCommand` | `reload` |
| `QuitSlashCommand` | `quit` |

Registration (`CreateRegistry`): every name registers a `SlashCommandDefinition(name, command.Description, command.ExecuteAsync)` in one `SlashCommandRegistry` (`BuiltInSlashCommandCatalog.cs:56-66`); the same registry is seeded by `SlashCommandRegistryFactory` with extension commands + `skill:<name>` + `prompt:<name>` and shared by interactive, RPC, and server-facing paths.

### 5.5 Session commands

Session lifecycle commands exist across surfaces: CLI flags (`--continue/--resume/--session/--fork/--no-session/--session-dir`), slash commands (`/new`, `/session`, `/fork`, `/clone`, `/tree`, `/resume`, `/name`), RPC commands (`new_session`, `switch_session`, `fork`, `get_fork_messages`, `set_session_name`), server commands (see §6), and extension hooks (`session_before_switch`, `session_before_fork`, `session_shutdown`). `SessionRuntime.NewSessionAsync`/`SwitchSessionAsync`/`ForkAsync` return `ExtensionSessionReplacementResult(Cancelled, Reason, SessionId, SessionFile)` (`src\PiSharp.Extensions\ExtensionParityContracts.cs`).

---

## 6. Server (`PiSharp.Server`)

### 6.1 Today (main branch)

`src\PiSharp.Server\Program.cs` (16 lines): registers `ApiKeyValidator`, `ServerSessionRegistry`, `PiServerWebSocketHandler`; maps `GET /health` → `Results.Ok(new { status = "ok" })` and `Map("/ws", ...)` → `PiServerWebSocketHandler.HandleHttpAsync`.

- **Auth**: `ApiKeyValidator` (`src\PiSharp.Server\Authentication\ApiKeyValidator.cs`) gates the WebSocket upgrade (`HandleHttpAsync` returns 401 *"Missing or invalid API key."* on failure); accepts `Bearer` header or `?access_token` query (constant-time comparison per `tests\PiSharp.Server.Tests\PiServerWebSocketHandlerTests.cs` coverage). `/health` is open.
- **Protocol**: JSON frames; `ServerCommandEnvelope(Type, Id?, ServerSessionId?)` in, `ServerResponse(Type, Id, Command, Success, Data?, Error?)` out (`src\PiSharp.Server\Contracts\ServerContracts.cs`).
- **Command set** (17, `ServerCommandTypes` in `ServerContracts.cs:8-27`): `create_session`, `dispose_session`, `prompt`, `steer`, `follow_up`, `queue_next_turn`, `abort`, `get_state`, `get_messages`, `list_sessions`, `set_model`, `set_thinking_level`, `compact`, `new_session`, `switch_session`, `fork`, `set_session_name`. Dispatch switch verified in `PiServerWebSocketHandler.DispatchTextCommandAsync` (`WebSockets\PiServerWebSocketHandler.cs:101-121`).
- **Event streaming shape**: one `ServerEventEnvelope` per event — `(Type: "event", ServerSessionId, Sequence, Timestamp, Event: AgentSessionEvent)` (`ServerContracts.cs:104-113`), serialized with `ServerJsonSerializer`. `LiveServerSession` (`src\PiSharp.Server\Runtime\LiveServerSession.cs`) holds a `ConcurrentDictionary<Guid, Channel<ServerEventEnvelope>> _subscribers` (per-session `eventCapacity` default 1024, drop-oldest), a monotonically increasing `_sequence`, a `SemaphoreSlim Gate` (`RunExclusiveAsync` serializes commands), `RequestAbort` (cancels harness without the gate), and `SnapshotAsync` → `ServerSessionState(ServerSessionId, RuntimeSessionId, RuntimeSessionPath, SessionName, Cwd, Model, ThinkingLevel, IsBusy, IsCompacting, MessageCount)`. The socket handler runs one event-pump task per live session per connection (`EnsureEventPumpAsync`, reading `live.ReadEventsAsync`). `ServerSessionRegistry` (`Runtime\ServerSessionRegistry.cs`) owns `LiveServerSession` instances and `list_sessions` (persisted + live sessions).
- **Registry & lifecycle** (`src\PiSharp.Server\Runtime\ServerSessionRegistry.cs`): `ConcurrentDictionary<string, LiveServerSession> _sessions` (Ordinal) + `_liveRuntimeSessionIds` reservation map; `CreateAsync` **builds a full `SessionRuntime` per server session** via `_runtimeFactory` (default `CreateRuntimeAsync`), validates the request (`ValidateCreateRequest`, `RejectPersistedSessionIdCollisionAsync`), rolls back reservations/runtimes on failure, and returns `ServerSessionCreated(ServerSessionId, Snapshot)`; `DisposeAsync(id)` disposes the runtime; `ListSessionsAsync` branches between the live registry and the persisted-session repo (`ListSessionsFromRepoAsync`); a global `DisposeAsync` tears down every live session. Sessions are scoped to the server process — nothing persists a running session across a server restart.
- **What does not exist today** (verified absence): no `attach` / `sinceSequence` replay / `resync`; no `run_command`/`complete_command`; no `process_input`; no `get_theme`/`get_session_snapshot`/`get_extension_*`; no idle-timeout disposal; no UI-bridge `ui_request`/`ui_response` lane; no daemon lease. None of these appear in `ServerCommandTypes` or the dispatch switch. Per `docs\plans\2026-08-14-daemon-client-architecture-design.md` (Context): *"but nothing connects to it today."* Also no `PiServerHost` library class exists on main — `Program.cs` wires `WebApplication.CreateBuilder` directly (the design doc plans a `PiServerHost` library refactor).

### 6.2 What the daemon design doc plans (summary)

`docs\plans\2026-08-14-daemon-client-architecture-design.md` (Status: Approved, design phase): split `pisharp` into a per-user **daemon** (evolved `PiSharp.Server`, one per user, lease at `~/.pi/PiSharp/daemon.json`, `pisharp daemon start/stop/status`) and a **TUI client** (new `PiSharp.Client` project with an event-sourced `ClientSessionState` reducer), communicating over `ws://127.0.0.1:<port>/ws` with api-key auth. **Client is a pure event-sourced view**: `ClientSessionState` is mutated only by an `Apply(event)` reducer (mirroring today's `TuiRenderState.Reduce`), plus one-time command results (`get_state` on attach, `get_session_snapshot`); it tracks `lastAppliedSequence` per session, re-attaching from its watermark on gaps; first attach starts at `head - replayWindow` (e.g. last 5000 events), older history via `get_messages`/JSONL.

Planned protocol additions (design doc §Wire protocol, lines 89-97): `attach { sessionId, sinceSequence }` (replay retained log, then live), `run_command` / `complete_command` (slash commands + completion, ported from RPC mode), `process_input` (user input hooks incl. bash `!` → `TuiInputHookResult`), `get_theme`, `get_session_snapshot` (metadata + branch entries), `get_fork_messages`, `get_extension_load_status`, `get_extension_shortcuts`, `get_extension_registry`, `resolve_tool`, `cycle_thinking_level`, `get_startup_messages` + `post_startup_checks` (npm outdated), `get_available_models`, `get_commands`, `get_last_assistant_text` (ported from RPC mode).

Key design decisions: (1) `run_command`/`prompt`/`compact`/`process_input` execute server-side inside `RunExclusiveAsync`; synchronous results ride the response, async lifecycle rides the event stream (`turn_start`, `message_*`, `tool_*`, `compaction_*`, …). (2) `LiveServerSession` gains a retained in-memory ring buffer (e.g. last 100k envelopes, indexable by sequence); if `sinceSequence` falls out of the buffer the daemon sends `resync` (full snapshot + truncated replay). (3) **UI-bridge round-trip generalized**: `IRequestExtensionUiAsync` blocks on a pending-UI request; the daemon pushes `ui_request` (own id) to attached clients, one answers `ui_response { requestId, response }`, the pending TCS resolves — mirrors RPC mode's `extension_ui_response` (§5.3); headless (no client attached) auto-declines `{ cancelled: true }` so turns don't hang; multi-client policy targets the most-recently-active client first. (4) **Idle-timeout disposal**: live session with zero attached clients and no pending turn is disposed after 5 min (configurable); in-progress turns run to completion; `get_state` returns 404 for disposed sessions → client falls back to `create_session` (resume from persisted JSONL). (5) Daemon bound to `127.0.0.1` on a chosen free port; logs to `~/.pi/PiSharp/logs` (separate daemon log file). Phased delivery: Phase 0 `PiServerHost` library refactor + daemon CLI scaffolding; Phase 1 server event sourcing (ring buffer, replay, resync); Phase 2 `PiSharp.Client` + reducer; Phase 3 `RemoteTuiBackend` + server command additions; Phase 4 lifecycle polish (idle timeout, graceful `daemon stop`, `--attach` UX).

---

## 7. Extension system (current state, exhaustive)

### 7.1 Native .NET extension contracts (`src\PiSharp.Extensions`)

Public types per file:

- **`IExtension.cs`** — `IExtension { Task InitializeAsync(IExtensionApi api, CancellationToken = default); }` (the only member).
- **`ExtensionMetadataAttribute.cs`** — `[AttributeUsage(Assembly | Class)]`, `Id` (required), `Name`, `Version` (default `"1.0.0"`), `Description`, `SourceId`.
- **`ExtensionDescriptor.cs`** — `ExtensionDescriptor(Id, Name, Version, Path?, Description?, SourceId?)`; `EffectiveSourceId` (`extension:<normalized-id>`); `FromMetadata`; `Validate()`.
- **`IExtensionApi.cs`** — properties `Descriptor`, `Cwd`, `HasUi`, `Ui: IExtensionUi`, `Session: IExtensionSessionApi`, `Tools: IExtensionToolApi`, `Skills: IExtensionSkillApi`, `Model: IExtensionModelApi`, `Events: IExtensionEventBus`, `Prompt: IExtensionPromptApi`; methods `On(eventName, handler)`, `Use(middleware)`, `RegisterTool`, `RegisterSkill`, `RegisterCommand`, `RegisterShortcut`, `RegisterFlag`, `RegisterMessageRenderer`, `RegisterMessageDecorator`, `RegisterProvider(IModelProvider): RegisteredApiProvider`, `RemoveProvider(api)`, `GetFlag(name)`, `GetFlags()`, `SendMessageAsync(message[, delivery, triggerTurn])`.
- **`ExtensionParityContracts.cs`** — `ExtensionMessageDelivery { Steer, FollowUp, NextTurn }`, `ExtensionFlagType { Boolean, String }`, `ExtensionOverridePolicy { Reject, Override, OverrideBuiltIn }`, `ExtensionCommandSourceInfo`, `ExtensionCommandInfo`, `ExtensionSessionReplacementResult`, `ExtensionCommandContext`, and interfaces:
  - `IExtensionSessionApi`: `SendMessageAsync` (x2), `SendUserMessageAsync(content, delivery = FollowUp)`, `AppendEntryAsync(customType, data)`, `GetNameAsync`, `SetNameAsync`, `SetLabelAsync(entryId, label)`.
  - `IExtensionToolApi`: `RegisterTool`, `GetActiveToolsAsync`, `GetAllToolsAsync`, `SetActiveToolsAsync`.
  - `IExtensionSkillApi`: `RegisterSkill`, `GetAllSkillsAsync`, `GetSelectedSkillsAsync`, `SetSelectedSkillsAsync`.
  - `IExtensionModelApi`: `SetModelAsync(ModelDescriptor)`, `GetThinkingLevelAsync`, `SetThinkingLevelAsync(ThinkingLevel)`.
  - `IExtensionEventBus`: `On(eventName, handler)`, `EmitAsync(eventName, payload)`.
  - `IExtensionPromptApi`: `RegisterContributor(IPromptContributor)`, `RegisterSection(PromptSection | ExtensionPromptSectionRegistration)`, `RegisterTransform(IPromptTransform)`.
  - Registrations: `ExtensionCommandRegistration(Name, Description, Handler)` / `(Name, ContextHandler)`, `ExtensionShortcutRegistration(Keys, Description, Handler)`, `ExtensionFlagRegistration(Name, Description, Type, DefaultValue)`, `ExtensionMessageRendererRegistration(Name, RowType, Handler?, Override, CustomType?)`, `ExtensionMessageDecoratorRegistration(Name, RowType, Handler?, Order, CustomType?)`, `ExtensionPromptSectionRegistration(Section, Override)`, `ExtensionWidgetState`, `ExtensionUiPlacementRecord`, `ExtensionResourcesDiscoverPayload/Result`, `ExtensionUserBashPayload/Result`, `ExtensionBashOperations/Result`.
- **`ExtensionToolRegistration.cs`** — `ExtensionToolRegistration(Name, Label, Description, ParametersSchema, ExecuteAsync, ExecutionMode?, PromptSnippet?, PromptGuidelines?, PrepareArguments?, RenderShell?, RendererName?, Override)`; `ToAgentTool()` → `ExtensionRegisteredTool : IAgentTool`.
- **`ExtensionSkillRegistration.cs`** — `ExtensionSkillRegistration(Name, Description, Content, FilePath, DisableModelInvocation, Override)`.
- **`ExtensionEvents.cs`** — `ExtensionEventNames` **42 name constants** (verified by grep; the runtime doc and earlier notes say 45 — current source has 42):
  - Session lifecycle: `session_start`, `resources_discover`, `input`, `user_bash`, `session_before_switch`, `session_before_fork`, `session_shutdown`.
  - Agent/turn/message: `agent_start`, `agent_end`, `turn_start`, `turn_end`, `message_start`, `message_update`, `message_end`.
  - Tool execution: `tool_execution_start`, `tool_execution_update`, `tool_execution_end`, `tool_call`, `tool_result`.
  - Harness state: `queue_update`, `compaction_start`, `compaction_end`, `auto_retry_start`, `auto_retry_end`, `session_info_changed`, `thinking_level_changed`, `save_point`, `abort`, `settled`.
  - Prompt/provider: `before_agent_start`, `before_prompt_render`, `context`, `before_provider_request`, `before_provider_payload`, `after_provider_response`.
  - Session tree/model: `session_before_compact`, `session_compact`, `session_before_tree`, `session_tree`, `model_select`, `thinking_level_select`, `resources_update`.
  - Also in this file: `ExtensionInputEvent(Text, Images?, Source)`, `ExtensionMiddlewareContext` (tool-call middleware), and the `ExtensionMiddlewareContext.Blocked/BlockReason/ModifyToolResult` hooks.
- **`ExtensionUi.cs`** — `IExtensionUi` (default-implemented): `RequestAsync(ExtensionUiRequest)`, `NotifyAsync`, `ConfirmAsync`, `InputAsync`, `SelectAsync`, `OnTerminalInput`, `SetStatusAsync`, `SetWidgetAsync`, `SetTitleAsync`, `GetEditorTextAsync`, `SetEditorTextAsync`, `SetWorkingMessageAsync`, `SetWorkingVisibleAsync`, `SetWorkingIndicatorAsync`, `SetHiddenThinkingLabelAsync`, `SetFooterAsync`, `SetHeaderAsync`, `RegisterMenuItemAsync`, `ShowCustomAsync`, `PasteToEditorAsync`, `OpenEditorAsync`, `AddAutocompleteProvider`; `ExtensionUiSeverity`, `ExtensionTerminalInputResult`, `ExtensionWorkingIndicator`, `ExtensionUiRequest/Result`, `ExtensionMenuItem`, and `NoExtensionUi` (singleton; every interactive member throws `NotSupportedException` — *"Extension UI is not available in this mode."*).
- **`ExtensionChatRows.cs`** — `ExtensionChatRowType { User, Assistant, AssistantThinking, ToolCall, ToolResult, System, Error, Custom, BridgeSlot, Unknown }`, `ExtensionChatRowKind`, `ExtensionChatSpanKind`, `ExtensionChatRowMaxWidthPolicy { Wrap, Clip }`, `ExtensionChatInteractionTarget`, `ExtensionChatSpan`, `ExtensionChatRowLayoutHints`, `ExtensionChatRow`, `ExtensionChatThemeToken`, `ExtensionChatRowRenderContext`, delegates `ExtensionMessageRenderHandler`/`ExtensionMessageDecorateHandler`.
- **`PromptDocumentPatches.cs`** — `PromptDocumentContentTypes { Raw, Markdown }`, `PromptDocumentSectionDto`, `PromptDocumentSectionPatch`, `PromptDocumentPatch(RemoveSectionIds?, ReplaceSections?, AppendSections?)`, `PromptDocumentHookPayload`.
- **`ExtensionRuntimeBinding.cs`** — `ExtensionResourceItem/Content`; `ExtensionRuntimeBinding` — the binding layer that maps `IExtensionApi` surface to runtime delegates (`SendMessageAsync`, `SendUserMessageAsync`, `AppendEntryAsync`, `GetSessionId/NameAsync`, `SetSessionNameAsync`, `SetLabelAsync`, `GetActive/AllToolsAsync`, `SetActiveToolsAsync`, `GetAll/SelectedSkillsAsync`, `SetSelectedSkillsAsync`, **`GetCommandsAsync`**, **`WaitForIdleAsync`**, **`NewSessionAsync`**, **`ForkSessionAsync`**, **`NavigateTreeAsync`**, **`SwitchSessionAsync`**, **`IsIdleAsync`**, **`HasPendingMessagesAsync`**, **`CompactAsync`**, **`GetSystemPromptAsync`**, **`AbortAsync`**, **`ShutdownAsync`**, `SetModelAsync`, `Get/SetThinkingLevelAsync`, `ReloadExtensionsAsync`, `EmitEventAsync`, `CreateAgentSessionAsync` + 9 `AgentSession*Async` child-session delegates, `OnChildSessionEventAsync`, `ResourceItems`, `ReadResourceAsync`, flags). Note: **`ExtensionRuntimeBinding` has delegates for the JS-pi command/session-control surface, but the native `IExtensionApi`/`IExtensionSessionApi` do not expose them** (see §7.5).
- **`ExtensionRegistry.cs`** — `OwnedExtensionRegistration<T>`, `ExtensionRegistryChangeKind { Added, Removed, Replaced, Restored, SourceRemoved }`, `ExtensionRegistryChange`, `ExtensionRegistry` with 13 registration collections: `Tools` (`tool:{name}`), `Skills` (`skill:{name}`), `Providers` (`provider:{api}`), `Handlers` (`handler:{event}:{guid}` + order), `Middleware`, `Commands`, `Shortcuts`, `Flags`, `Renderers` (row/name/custom keys), `Decorators`, `PromptContributors`, `PromptSections` (`prompt-section:{id}`), `PromptTransforms`; `BuiltInToolNames`, `SourceIds`, `Changed` event, `DispatchAsync`, `UnregisterBySource`; override policy enforced via last-wins `RegistrationStack<T>` (duplicate + `Reject` → `InvalidOperationException`; built-in tool names require `OverrideBuiltIn`).
- **`ExtensionManager.cs`** — `ExtensionRuntimeActions`, `ExtensionManager.InitializeAsync(descriptor, extension, actions|binding, ct)` (validates descriptor → builds private `ExtensionApi` → calls `InitializeAsync` → tracks `LoadedExtension`), `Unload(sourceId)` (unregisters registry + providers, re-registers surviving providers).
- **`ExtensionRegistryChangeStream.cs`** — `IExtensionRegistryChangeStream`, `ExtensionRegistryChangeDeliveryFailure`, fan-out implementation.
- **`ExtensionEventBus.cs`** — `ExtensionEventBus : IExtensionEventBus, IDisposable` (registration-ordered dispatch, failure isolation into `Diagnostics`, optional bridge emitter).

**What a native extension can register** (all via `IExtensionApi`): tools, skills, slash commands, keyboard shortcuts, CLI flags, message renderers/decorators, model providers (`IModelProvider`), prompt contributors/sections/transforms, event handlers, middleware, and (via `Session`) messages/custom entries/labels/session names. UI via `IExtensionUi` (notifications, confirm/input/select dialogs, status/widgets, title/header/footer, editor text ops, working indicator, autocomplete, custom component overlays, menu items, generic `RequestAsync`). **There is no settings API and no extension-owned persistent state store on `IExtensionApi`** (verified absence, see §7.5).

### 7.2 Plugin host (`src\PiSharp.PluginHost`)

`NativePluginHost(PluginHostOptions)` (`PluginHost.cs`): `PluginHostOptions(PluginDirectories, ExplicitPluginPaths, HotReload)`; `FromCwd` builds plugin dirs `<cwd>/plugins`, `<cwd>/.pi/extensions`, and `~/.pi/extensions` (home); explicit `--extension <path>.dll` entries go through `ExplicitPluginPaths`. `Discover()` = explicit paths + `Directory.EnumerateFiles(dir, "*.dll", AllDirectories)`. `Load(assemblyPath)`: full path → `new PluginLoadContext(fullPath)` (**collectible** `AssemblyLoadContext(isCollectible: true)` with `AssemblyDependencyResolver`, `PluginLoadContext.cs`) → `LoadPluginAssembly()` → `ReadMetadata` (requires `ExtensionMetadataAttribute` at assembly level, falls back to type level; throws otherwise) → first concrete `IExtension` type → `Activator.CreateInstance` → `LoadedNativePlugin(Descriptor, Extension, Context, LoadContextReference)`. `Unload(sourceId)` moves the load-context reference to a `WeakReference` and calls `Context.Unload()`; `IsUnloaded` runs GC up to 10×. Pitfalls documented: parameterless ctor required; unload requires no lingering references (`docs\pisharp-native-extensions.md` §Unload and reload notes).

### 7.3 TypeScript bridge (`src\PiSharp.TsBridge`)

- Host: `TsExtensionHost(TsBridgeOptions, ExtensionRegistry, ExtensionRuntimeBinding?, ILoggerFactory?)`. Startup (`StartAsync`): builds `initializePayload` **including `bridgeManifest = TsBridgeManifestFactory.CreateDefault()`**, starts `node Node/TsBridgeRunner.mjs` via `NodeTsBridgeClient` (JSON-RPC over stdio). JSON-RPC methods (15): `register_tool`, `register_skill`, `register_provider`, `unregister_provider`, `register_command`, `register_shortcut`, `register_flag`, `register_prompt_section`, `register_prompt_transform`, `register_message_renderer`, `unregister_message_renderer`, `register_message_decorator`, `unregister_message_decorator`, `runtime_action`, `ui_request`.
- Runtime actions (48, `TsBridgeRuntimeActions`): `get_all_skills`, `get_selected_skills`, `set_selected_skills`, `get_flag`, `get_flags`, `get_active_tools`, `get_all_tools`, `get_commands`, `wait_for_idle`, `new_session`, `fork_session`, `navigate_tree`, `switch_session`, `is_idle`, `has_pending_messages`, `compact`, `get_system_prompt`, `abort`, `shutdown`, `exec`, `get_thinking_level`, `send_message`, `send_user_message`, `append_entry`, `set_entry_label`, `get_session_name`, `set_session_name`, `set_active_tools`, `set_model`, `set_thinking_level`, `reload_extensions`, `emit_event`, `list_resources`, `read_resource`, `complete_simple`, `prompt_and_wait`, `create_agent_session`, `agent_session_prompt`, `agent_session_steer`, `agent_session_follow_up`, `agent_session_abort`, `agent_session_compact`, `agent_session_set_model`, `agent_session_set_thinking_level`, `agent_session_dispose` (verified in `TsExtensionHost.RuntimeActionAsync`; SDK-shim alias mapping in `SdkShimRuntimeDispatcher`).
- Manifest/shim system: `TsBridgeManifestContracts.cs` defines `TsBridgeManifest(SchemaVersion, ModuleShims, Protocol, ApiSurface)`; export kinds (`helper`, `json-const`, `unavailable-function`, `async-unavailable-function`, `runtime-function`, `namespace`); statuses (`implemented`, `snapshot`, `runtime-action`, `stub-unavailable`); `SchemaVersion = 1`. Shimmed npm specifiers (9): `@pi-ai`, `@earendil-works/pi-ai`, `@mariozechner/pi-ai`, `@pi-tui`, `@earendil-works/pi-tui`, `@mariozechner/pi-tui`, `@pi-coding-agent`, `@earendil-works/pi-coding-agent`, `@mariozechner/pi-coding-agent` (`TsBridgeManifestFactory.CreateDefault`). Node side generates shim `.mjs` files (sha256-hashed) under the bridge cache via `src/shims/materialize.ts`/`codegen.ts`; import rewriting in `src/runner/importRewriter.ts` + `transpiler.ts`. The C# manifest is the parity contract: `tests\PiSharp.TsBridge.Tests\TsBridgeManifestTests.cs` enforces *"no roadmap or false unsupported statuses"* (`BridgeManifestDoesNotContainRoadmapOrFalseUnsupportedStatuses`).
- Descriptor cache: `TsDescriptorCache` at `<global-pi>/cache/ts-bridge/descriptors/<sha256(extensionPath)>.json` (wired in `PiRuntimeBootstrap.cs:138`); replay skipped when schema/source/dependency hash mismatch, live services involved, or `activation: "eager"`. Activation: `"auto"` (lazy per-tool via `TsBridgeTool.EnsureExtensionActivatedAsync`), `"eager"` (or declares provides/consumes services) → background activation batch.
- Event forwarding: main-session events queued via `Channel<ChildSessionEventForward>` (batch 64/16 ms) for non-mutating events; mutating hooks (`before_prompt_render`, `before_agent_start`, `input`, `session_before_switch`, `session_before_fork`, `session_shutdown`) use awaited request/response (registered in `SessionRuntime.cs:154-177`).
- Extension services: `pi.extensions.provide/get/waitFor/declare` — live in-process JS objects, never serialized through .NET; eager activation for providers/consumers.
- Child sessions: `createAgentSession()` from `@pi-coding-agent` → in-process `AgentSession` proxy backed by the runtime subagent service (see §10).

### 7.4 The three shipped extensions (`extensions\`)

- **`workflow-sessions`** (`extensions\workflow-sessions\src\index.ts` + `README.md`): registers the **`workflow_run`** model-callable tool and the eager **`pisharp.workflows`** service (`runNode`, `runDag`, `validateDag`, `listRuns`, `getRun`). Each workflow node runs as a separate child `pisharp --print` process (default `--no-extensions`); DAG validation (duplicate ids, unknown deps, cycles), blocked descendants, `maxConcurrency` default 2. Env: `PISHARP_WORKFLOW_PISHARP_BIN` (default `pisharp`), `PISHARP_WORKFLOW_STATE_DIR` (default `<cwd>/.pi/workflows`). Metadata: `<state>/workflow-runs.jsonl` (`created|running|completed|failed|cancelled|blocked` events); parent-session audit via `pi.appendEntry("workflow:node", ...)`.
- **`pisharp-embeddings`** (`extensions\pisharp-embeddings\src\index.ts`; no README): provides **`pisharp.embeddings`** v1 service (`registerProvider`, `listProviders`, `getDefaultProvider`, `embed`, `embedMany`); ships `createOpenAICompatibleProvider` (`POST {baseUrl}/embeddings`, Bearer key; localhost exempt). Env: `PISHARP_EMBEDDINGS_PROVIDER` (default `openai`), `PISHARP_EMBEDDINGS_BASE_URL` (default `https://api.openai.com/v1`), `PISHARP_EMBEDDINGS_API_KEY` (fallback `OPENAI_API_KEY`), `PISHARP_EMBEDDINGS_MODEL` (default `text-embedding-3-small`). No tools registered.
- **`relevance-filtered-skills`** (`extensions\relevance-filtered-skills\src\index.ts`; no README): consumes **`pisharp.embeddings`**; on `before_prompt_render` finds section `skills.available`, embeds `name + description` (document) and the prompt (query), cosine-ranks, keeps `score >= minScore` top `maxSkills`, returns `{ patch: { replaceSections: [...] } }`; fails open (no patch) on missing service/ranking errors. Env: `PISHARP_SKILL_RELEVANCE_MAX_SKILLS` (8), `PISHARP_SKILL_RELEVANCE_TIMEOUT_MS` (5000), `PISHARP_SKILL_RELEVANCE_MIN_SCORE` (-1). No tools registered. Load order matters: embeddings first, then selector (`docs\pisharp-typescript-extensions.md` §Relevance-filtered skills extension).

### 7.5 Verified gaps in the extension surface (absence-based)

Only items verifiable by absence in the contracts are listed; each states where it was looked for.

1. **No settings/config API.** `IExtensionApi` (`src\PiSharp.Extensions\IExtensionApi.cs`) exposes no settings read/write; no `ISettings`/`Configuration` member appears anywhere in `src\PiSharp.Extensions\`. Extensions cannot read or write PiSharp settings.
2. **No extension-owned persistent state / key-value store.** Not found in `src\PiSharp.Extensions\` (no `State`, `Storage`, `KV` types). The only persistence-adjacent APIs are session-entry appends (`IExtensionSessionApi.AppendEntryAsync`, `AppendCustomMessageEntryAsync`).
3. **No `GetCommands` on the native `IExtensionApi`.** `ExtensionRuntimeBinding.GetCommandsAsync` exists (`src\PiSharp.Extensions\ExtensionRuntimeBinding.cs:49`) but nothing on `IExtensionApi`/`IExtensionSessionApi` surfaces it. (The TS bridge does expose `get_commands` runtime action; see §12 for parity notes.)
4. **No session-control on `IExtensionSessionApi`.** `NewSessionAsync`, `ForkAsync`, `SwitchSessionAsync`, `NavigateTreeAsync`, `WaitForIdleAsync` are **not** in `IExtensionSessionApi` (`src\PiSharp.Extensions\ExtensionParityContracts.cs:46-56`); they exist only as `ExtensionRuntimeBinding` delegates.
5. **No theme API.** No theme get/set on `IExtensionApi`/`IExtensionUi`; the TUI theme is internal (`src\PiSharp.Tui\Interactive\Theme\`). `IExtensionUi` has no `GetTheme`/`SetTheme`/`GetAllThemes`.
6. **No tools-expanded state API.** `getToolsExpanded`/`setToolsExpanded` not in `IExtensionUi` (`src\PiSharp.Extensions\ExtensionUi.cs`).
7. **No full editor-component API.** `SetEditorComponent`/`GetEditorComponent` absent; only text get/set/paste/open (`ExtensionUi.cs:17-18,27-28`).
8. **No skill *definition* beyond markdown registration.** `IExtensionSkillApi.RegisterSkill` takes `ExtensionSkillRegistration` (name/description/content/file) only; no structured skill pipeline, no per-skill runner hook.
9. **No package/install API for extensions at runtime.** Extension install is a CLI-only package command (`src\PiSharp.Cli\Packages\`); nothing on `IExtensionApi`.
10. **No tracing/metrics API.** No OpenTelemetry/tracing surface in `src\PiSharp.Extensions\` (see §8 for observability state).
11. **No custom message *renderer for RPC/print*.** Renderers/decorators affect the TUI chat-row pipeline only; registrations are accepted in non-UI modes but are inert (doc-stated: `docs\pisharp-typescript-extensions.md` §Supported registrations; `docs\pisharp-vs-pi.md`).
12. **No persistent message queue / delivery guarantees for coordination** (extension-level): daemon messages are best-effort JSONL replay (`docs\pisharp-agent-coordination.md` §Known limits).

Also note: `docs\analysis\pisharp-extension-api-inventory.md` (older) lists TS-bridge gaps (`pi.getCommands()` missing, `ctx.sessionManager.getBranch()` stubbed, `ctx.isIdle()` hard-coded true, `ctx.newSession()` missing, etc.). **That inventory is stale on several points**: current `TsExtensionHost.RuntimeActionAsync` handles `get_commands`, `wait_for_idle`, `new_session`, `fork_session`, `navigate_tree`, `switch_session`, `is_idle`, `has_pending_messages`, `compact`, `get_system_prompt`, `abort`, `shutdown`, `exec`, and the current `RpcMode` uses the shared `SlashCommandRegistryFactory` (skills included). Treat `src\PiSharp.TsBridge` + `tests\PiSharp.TsBridge.Tests\TsBridgeManifestTests.cs` as authoritative for the bridge surface.

### 7.6 TypeScript extension `pi` root API surface (current)

Implemented by `Node\src\runner\piApi.ts` (built from `createPiApi(...)`); verified against `docs\analysis\pisharp-extension-api-inventory.md` (which inspected `piApi.mjs`/`uiApi.mjs`) and the runtime-action list in §7.3:

- **Root members**: `cwd`; `on(event, handler)`; `events.on`/`events.emit`; `registerCommand(name, options|description, handler)`; `registerShortcut(keys, options, handler)`; `registerFlag(name, options)`; `prompt.registerSection(section)` / `prompt.registerTransform(transform)`; `registerMessageRenderer(...)` / `registerMessageDecorator(...)` (disposable); `registerTool(tool)`; `registerProvider(config)` / `unregisterProvider(name)`; `registerSkill(skill)`; `skills.list()/selected()/select(names)/register(skill)`; `extensions.provide/get/waitFor/declare`; `getFlag(name)` / `getFlags()`; `getThinkingLevel()`; `getActiveTools()` / `getAllTools()` / `setActiveTools(names)`; `setModel(model)` / `setThinkingLevel(level)`; `sendMessage(message, options?)` / `sendUserMessage(content, options?)`; `appendEntry(type, data)`; `session.getName()/setName(name)/appendEntry/setEntryLabel`; `resources.list()` / `resources.read(path)`; `reload()`; `ui` (`Node\src\runner\uiApi.ts` — notify/toast/confirm/prompt/input/select/markdown/details/progress/setStatus/status/setFooter/setHeader/panel/setWidget/theme).
- **Delivery options for `sendMessage`** (doc-verified, `docs\pisharp-typescript-extensions.md` §Custom Messages): `{ deliverAs: "nextTurn" | "steer" | "followUp" }` and `{ triggerTurn: true }`; no second argument → append as visible `CustomMessageEntry` when `display: true`.
- **Command/event context `ctx`** (built by `Node\src\runner\extensionContext.ts`): `extensionId`, `cwd`, `hasUI`, `ui`, `sessionManager`, `isIdle()`, `abort()`, `hasPendingMessages()`, `shutdown()`, `getContextUsage()`, `compact()`, `getSystemPrompt()`, `signal` — all backed by the runtime actions listed in §7.3 (snapshot fields from `RuntimeExtensionBinder.BuildSessionSnapshotAsync`: `sessionManager.getEntries/getBranch/getLeafId/getLeafEntry/getEntry/getTree/getChildren/getLabel/getHeader/getSessionName/getCwd/getSessionDir/getSessionId/getSessionFile/isPersisted`, `ctx.model`/`ctx.modelRegistry`, `ctx.getContextUsage()` — per `docs\pisharp-typescript-extensions.md` §Runtime snapshot parity).

The older inventory doc's "missing root members" list (`pi.getCommands()`, `pi.exec()`, root `setSessionName`/`getSessionName`/`setLabel`, `pi.hasUI`) is superseded by the current runtime-action wiring for `get_commands`/`exec`/session-name/label actions and `ctx.hasUI` (§7.3, §12). Sync-vs-async divergences from JS pi remain (see §12.3).

---

## 8. Hooks / events / observability

- **Event union**: `AgentHarnessEvent` = `Core(AgentEvent)` | `Own(AgentHarnessOwnEvent)` (`src\PiSharp.Agent.Core\Events\AgentHarnessEvent.cs`). `AgentEvent` has 10 variants (`Events\AgentEvent.cs`): `AgentStart`, `AgentEnd`, `TurnStart`, `TurnEnd`, `MessageStart`, `MessageUpdate`, `MessageEnd`, `ToolExecutionStart`, `ToolExecutionUpdate`, `ToolExecutionEnd`. `AgentHarnessOwnEvent` has 24 variants (same file, lines 102-241): `SessionStart`, `Input`, `SessionBeforeSwitch`, `SessionBeforeFork`, `SessionShutdown`, `QueueUpdate`, `CompactionStart/End`, `AutoRetryStart/End`, `SessionInfoChanged`, `ThinkingLevelChanged`, `SavePoint`, `Abort`, `Settled`, `BeforeAgentStart`, `BeforePromptRender`, `Context`, `BeforeProviderRequest`, `BeforeProviderPayload`, `AfterProviderResponse`, `ToolCall`, `ToolResult`, `SessionBeforeCompact`, `SessionCompact`, `SessionBeforeTree`, `SessionTree`, `ModelSelect`, `ThinkingLevelSelect`, `ResourcesUpdate`. `AgentSessionEvent` is the flat JavaScript-compatible shape used at the RPC/server boundary (`AgentHarnessEvent.cs:21-85`).
- **Extension hooks**: `ExtensionEventNames` (42 constants, verified by grep — §7.1; the runtime doc says 45); mutating hooks (`before_prompt_render`, `before_agent_start`, `input`, `user_bash`, `session_before_switch`, `session_before_fork`, `session_shutdown`, `resources_discover`, `tool_call`/`tool_result` via middleware) can change behavior; all others are notifications. Middleware wraps tool calls/results (`ExtensionMiddlewareContext.Blocked/BlockReason/ModifyToolResult`).
- **Harness listeners**: `AgentHarness.Subscribe(...)`; TUI subscribes and batches; TypeScript bridge forwards events to Node; server converts to `ServerEventEnvelope`.
- **Logging**: structured `ILogger<T>` throughout (e.g. `Session<TMetadata>` logs `LogDebug` with named properties, `Session.cs:84-89`). File logging is **plain text, not structured JSON**: `CliFileLogging` (`src\PiSharp.Cli\Logging\CliFileLogging.cs`) + `RollingFileLoggerProvider` (`Logging\RollingFileLoggerProvider.cs`); default path `~/.pi/PiSharp/logs/pi.log`, session-scoped `~/.pi/PiSharp/logs/<encoded-cwd>/<session>.log` (`CliFileLogging.cs:50-61`); env `PISHARP_LOG_FILE`, `PISHARP_LOG_LEVEL`, `PISHARP_LOG_MAX_FILES`; settings section `logging` (`PiLoggingSettings` in `src\PiSharp.Compatibility\Settings\`). `--benchmark-startup` prints startup timings (`StartupBenchmarkFormatter`). A structured-logging plan exists (`docs\plans\2026-06-01-structured-logging-plan.md`) but the implementation is the plain-text rolling provider. **No tracing/OTel instrumentation found** in `src\PiSharp.Extensions\` or the runtime (absence-based).
- **Startup telemetry**: `--benchmark-startup` collects `StartupBenchmarkReport(Total, Phases, NativeExtensions, TypeScriptExtensions)` via `StartupBenchmarkCollector` (`src\PiSharp.Runtime\Runtime\StartupBenchmarkCollector.cs`) — per-phase stopwatch (`RuntimeStartupContext.MeasureAsync`), per-extension load/initialize timings (native + TS bridge timings), rendered to stderr by `StartupBenchmarkFormatter.Render` (`src\PiSharp.Cli\Runtime\StartupResourceSummary.cs`). `PiRuntimeBootstrap.CreateRuntimeAsync` passes the collector through `RuntimeStartupContext(options, benchmark)`; `Program.cs:88-91` prints it after runtime creation.
- **Startup diagnostics**: `RuntimeStartupContext.Diagnostics` collects `RuntimeDiagnostic` items (type + message); `Program.cs:93-98` prints `{Type}: {Message}` to stderr and exits with code 2 when any `Error`-type diagnostic exists (e.g. extension flag misconfigurations). Extension load status is also queryable at runtime: `SessionRuntime.GetExtensionLoadSummary()` (`ExtensionLoadSummary.From(ExtensionLoadCoordinator.Statuses)`) and `ExtensionLoadCoordinator` (`src\PiSharp.Runtime\Extensions\ExtensionLoadCoordinator.cs`).
- **Prompt debugging**: `SessionRuntime.LastPromptDebugView` — `PromptDebugView.FromDocument(Harness.LastPromptDocument)` for inspecting the final prompt document (`SessionRuntime.cs:93`).

---

## 9. Config & settings

- **Layers** (`PiSettingsStore`, `src\PiSharp.Compatibility\Settings\`; `docs\pisharp-runtime.md` §Settings layers; `docs\pisharp-vs-pi.md` §Settings differences): later wins — (1) global legacy `~/.pi/agent/settings.json`, (2) global PiSharp `~/.pi/PiSharp/settings.json`, (3) project legacy `<cwd>/.pi/settings.json`, (4) project PiSharp `<cwd>/.pi/PiSharp/settings.json`. Array settings replace earlier arrays except via `pisharp.append` (appendable: `extensions`, `skills`, `promptTemplates`, `themes`, `packages`; de-duplicated case-insensitively).
- **Setting keys** (doc-listed, verified in `PiSettingsStore`): `defaultProvider`, `defaultModel`, `defaultThinking`, `sessionDir`, `extensions`, `skills`, `promptTemplates`, `themes`, `packages`, `noExtensions`, `noSkills`, `noPromptTemplates`, `noThemes`, `noContextFiles`, `offline`, plus `logging` (file logging section).
- **Paths** (`PiAgentPaths.FromCwd`, `src\PiSharp.Compatibility\Settings\PiAgentPaths.cs`): `~/.pi/agent`, `<cwd>/.pi`, `~/.pi/PiSharp`, `<cwd>/.pi/PiSharp`, auth `~/.pi/agent/auth.json`, models `~/.pi/agent/models.json`, keybindings `~/.pi/agent/keybindings.json` (path defined; **TUI does not read it** — verified absence in survey), sessions `~/.pi/agent/sessions`, TS bridge cache `~/.pi/PiSharp/cache/ts-bridge`.
- **Context files**: `AGENTS.md`, `AGENTS.MD`, `CLAUDE.md`, `CLAUDE.MD` discovered in the global agent dir and every cwd ancestor; disable `--no-context-files`/`-nc` (`docs\pisharp-runtime.md` §Context and prompt files). System prompt files `.pi/SYSTEM.md` + `~/.pi/agent/SYSTEM.md`; append prompts `.pi/APPEND_SYSTEM.md` + `~/.pi/agent/APPEND_SYSTEM.md`.
- **Skills system**: exists. `SkillManager` (`src\PiSharp.Agent\Resources\SkillManager.cs`) loads `SKILL.md` files (or direct `.md` files when enabled) from directories, with YAML frontmatter (YamlDotNet), ignore files (`.gitignore`/`.ignore`/`.fdignore`), `Skill(Name, Description, Content, FilePath, DisableModelInvocation)`, `FormatInvocation` (wraps content in `<skill name=... location=...>`). Discovery paths (`PiResourceLoader`, `src\PiSharp.Compatibility\Resources\PiResourceLoader.cs`): `~/.pi/agent/skills`, `~/.agents/skills`, each ancestor's `.agents/skills` and `.pi/skills`, `settings.skills`, `--skill`, package `pi.skills`. Selection: `SetSelectedSkills`/`skill:<name>` slash commands; skills are injected into the system prompt (`skills.available` section). Design doc: `docs\plans\2026-05-26-skills-extensions-loading.md`.
- **Prompt templates**: `PromptTemplateCatalog`/`PromptTemplateEngine` (`src\PiSharp.Agent\Resources\PromptTemplateCatalog.cs`, `PromptTemplateEngine.cs`); `/prompt:<name>` slash commands and prompt-template commands from `SlashCommandRegistryFactory`; sources: settings `promptTemplates`, CLI `--prompt-template`, and package resources (`PackageResources(packageRoots, "prompts", "prompts")` and `PackageResources(packageRoots, "promptTemplates", "prompt-templates")` — `src\PiSharp.Compatibility\Resources\PiResourceLoader.cs:66`). Catalog is a case-insensitive dictionary of `PromptTemplateRegistration(Name, Description?, Content, SourcePath)` (`PromptTemplateCatalog.cs:8-30`); `LoadAsync` walks directories via `PromptTemplateEngine.LoadAsync` and reports per-path `PromptTemplateDiagnostic` records (`warning`/`stat_failed`); `FormatInvocation(name, args)` throws `InvalidOperationException($"Prompt template '{name}' was not loaded.")` for unknown names (`PromptTemplateCatalog.cs:31-34`). Prompt templates render into the system prompt alongside skills (`SystemPromptComposer`, §2.8; `Prompting\Contributors\SkillsPromptContributor.cs`).
- **Themes**: `src\PiSharp.Agent\Resources\Theme\` (theme documents); first matching theme loaded at startup; theme paths from settings/CLI/packages/`resources_discover`. No theme API for extensions (§7.5).
- **Profiles**: none found — no profile abstraction in `PiSettingsStore` or `CliArgs` (absence-based).
- **Keybindings**: `HotkeysSlashCommand` lists them; `~/.pi/agent/keybindings.json` path exists but is unread (absence-based, survey-verified).
- **`--no-resources`** expands to `--no-extensions --no-skills --no-prompt-templates --no-themes --no-context-files` (CLI-only; does not disable tools) (`docs\pisharp-runtime.md` §Resource-debug shortcut).

---

## 10. Subagents

- **Runtime service**: `SubagentSessionService` (`src\PiSharp.Runtime\Subagents\SubagentSessionService.cs`) creates isolated child sessions — each child gets its own `SessionRuntime`-view/`AgentHarness`, JSONL session, model/thinking state, cancellation scope, and event subscription list; `SubagentSessionHandle` (`Subagents\SubagentSessionHandle.cs`) + `SubagentSessionOptions(Model, ThinkingLevel, SessionName, ParentSessionPath)`. Child controls routed by `sessionId` without mutating the parent harness queue: `prompt`, `steer`, `followUp`, `abort`, `compact`, `setModel`, `setThinkingLevel`, `dispose` (binding delegates in `ExtensionRuntimeBinding.cs:66-75`).
- **Service internals** (verified, `SubagentSessionService.cs:42-73`): `CreateAsync(SubagentSessionOptions, ct)` builds a child session via `_runtime.SessionRepo.CreateAsync` with `PersistImmediately = true`, `Id = null` (fresh id) and `ParentSessionPath` inherited from the parent (`options.ParentSessionPath ?? _runtime.CreateOptions.ParentSessionPath`); model/thinking default to the parent harness (`options.Model ?? _runtime.Harness.Model`) then applied via `harness.SetModelAsync(model, "subagent", ct)` / `SetThinkingLevelAsync`; a `SubagentSessionHandle` is registered in a `ConcurrentDictionary<string, SubagentSessionHandle> _handles`. Subscribers per session are `SessionSubscriberState` (callback list + harness subscription) and the service disposes harnesses on `DisposeAsync`. `SubagentPromptResult` carries `(MessageCount, InputTokens?, OutputTokens?, TotalDuration?)` (`Subagents\SubagentPromptResult.cs`) — the token/usage accounting used by `PiSubagentsEventAdapter`.
- **Event translation**: `JsPiSubagentEventTranslator` (`Subagents\JsPiSubagentEventTranslator.cs`) maps `AgentEvent` → JavaScript Pi `AgentSessionEvent` records; same translator feeds TypeScript `AgentSession.subscribe()` listeners and the CLI `subagent-json` / `--mode json -p --no-session` compatibility mode (`docs\pisharp-runtime.md` §Subagent sessions). Child events are delivered asynchronously and batched (`ChildSessionEventBatchWorker` in `tests\PiSharp.TsBridge.Tests`).
- **Discovery**: no subagent discovery/tooling in core. Subagent support is: (a) TypeScript `createAgentSession()` (bridge), (b) CLI child processes via `--mode subagent-json`. The full-multiturn subagents plan (`docs\plans\2026-06-02-full-multiturn-subagents.md`) describes the JS-ecosystem `pi-subagents` extension model; the runtime's in-process service covers the `createAgentSession` API.
- **Coordination observation**: `PiSharp.Coordination` observes `subagents:created|started|completed|failed|steered|compacted` events via `PiSubagentsEventAdapter` → `SubagentObservedRecord` (id, type, description, status, durationMs, toolUses, inputTokens, outputTokens, parentSessionId, cwd) — compatible with `@tintinweb/pi-subagents` (`docs\pisharp-agent-coordination.md` §Subagent event observation; `src\PiSharp.Coordination\PiSubagentsEventAdapter.cs`). Known limits: `isolated: true` and `extensions: false` subagents are invisible to coordination.
- **Typed outputs**: none beyond the bridge `AgentSession.messages` (completed `AgentMessage` transcript) and session-tree entries (`docs\pisharp-typescript-extensions.md` §In-process subagent sessions); no schema/typed-output contract exists for subagents (absence-based).

---

## 11. In-flight work: daemon-client architecture (NOT in main)

Everything below lives only in the separate worktree `G:\code\AI\pi\PiSharp\.worktrees\daemon-client-architecture\` and is **not current state** of `main`. Main-branch `git status --porcelain` is empty; `src\PiSharp.Client\` does not exist on main (glob: no matches for `src/**/DaemonMode.cs`, `src/**/DaemonLauncher.cs`, `src/PiSharp.Client/**`).

Uncommitted worktree changes (from `git status --short` in that worktree):

- `M src\PiSharp.Cli\Modes\DaemonMode.cs` — `DaemonMode` (`start|stop|status`), port/api-key args, `--foreground`, lease store at `~/.pi/PiSharp/daemon.json` via `DaemonLeaseStore`, `DaemonLock.TryAcquire(store.LockPath)`, spawns detached via `DaemonLauncher.StartDaemonAsync(...)`, health-check timeout 10 s, `RunForegroundAsync` hosts `PiServerHost(new PiServerHostOptions { ApiKey })`. `stop`/`status` are `NotImplementedAsync` stubs at this point.
- `M src\PiSharp.Cli\Parsing\CliArgs.cs` — `DaemonCommandArgs` gains `ApiKey` (diff: `+ string? ApiKey = null`).
- `M src\PiSharp.Cli\Parsing\CliParser.cs` — parses `daemon ... --port <n>` and new `--api-key` for daemon commands.
- `M src\PiSharp.Cli\PiSharp.Cli.csproj` — adds `<ProjectReference Include="..\PiSharp.Client\PiSharp.Client.csproj" />`.
- `M src\PiSharp.Ai\Models\Generated\BuiltInModels.g.cs` — regenerated catalog (13,882-line diff; new model entries).
- `M tests\PiSharp.Cli.Tests\Parsing\CliParserTests.cs` — parser tests for daemon args.
- `?? src\PiSharp.Client\DaemonLauncher.cs` — untracked; starts the daemon process detached and polls `/health`.
- `?? tests\PiSharp.Cli.Tests\Modes\DaemonModeTests.cs`, `?? tests\PiSharp.Client.Tests\DaemonLauncherTests.cs` — untracked tests.

The worktree branch also contains tracked `src\PiSharp.Client\` project files (`PiSharp.Client.csproj`, `DaemonLeaseStore.cs`, `DaemonDiscovery.cs`, `DaemonLease.cs`) and `PiSharp.Server\Hosting\PiServerHost` (referenced by `DaemonMode`) — all of it in-flight relative to main. The approved design doc is `G:\code\AI\pi\PiSharp\docs\plans\2026-08-14-daemon-client-architecture-design.md` (Status: Approved; see §6.2 summary).

---

## 12. Summary of verified gaps vs the original JS pi

Explicitly documented gaps (docs that call them out, verified against current source where noted):

1. **TS-bridge command/session-control parity** (`docs\analysis\pisharp-js-pi-extension-parity-gaps.md`, `docs\analysis\pisharp-extension-api-inventory.md`): JS `pi.getCommands()`, `ctx.waitForIdle()`, `ctx.newSession({ withSession })`, replacement-session `ctx.sendUserMessage()`, live `ctx.sessionManager` reads, `ctx.isIdle()`/`hasPendingMessages()`/`getContextUsage()`/`getSystemPrompt()`, `ctx.modelRegistry`/`ctx.model`. **Partially superseded**: current `TsExtensionHost` handles `get_commands`, `wait_for_idle`, `new_session`, `fork_session`, `navigate_tree`, `switch_session`, `is_idle`, `has_pending_messages`, `compact`, `get_system_prompt`, `abort`, `shutdown`, `exec` runtime actions, and `docs\pisharp-typescript-extensions.md` §Runtime-action parity wires `sessionManager` reads to live snapshots (§7.3, §7.6).
   - **Command shape** (`RpcMode.cs:218-237`): `get_commands` now returns skill/prompt/extension commands mapped to JS-style `source` + `sourceInfo`, but `Scope` is hard-coded `"temporary"` and `Origin` hard-coded `"top-level"` — a JS pi entry carries real `scope: "user"|"project"|"temporary"` and `origin: "package"|"top-level"`. Synthesizing real scope/origin is still open.
   - **Replacement-session contexts**: JS `ctx.newSession({ withSession })`/`fork`/`switchSession` invoke a callback bound to the replacement session with `sendMessage`/`sendUserMessage`. PiSharp runtime actions perform the session operations, but a `withSession` replacement-context mechanism in the TS command context was not found in current bridge sources ([INFERENCE] — `extensionContext.ts` snapshot fields listed in §7.6 do not include one; the parity doc's *"replacement-session `ctx.sendUserMessage()`"* gap is only partially covered by the root `sendUserMessage` action).
   - **`ctx.waitForIdle()`**: runtime action `wait_for_idle` exists; delivery semantics (queue-correct waits during streaming) need parity verification per the parity doc.
2. **TS-bridge UI gaps** (inventory doc §UI parity gaps; TS extensions doc §Limitations): `setWorkingMessage/Visible/Indicator`, `setTitle`, editor/autocomplete surfaces, `custom`, `getAllThemes/getTheme/setTheme`, `getToolsExpanded/setToolsExpanded`, `italic` theme helper — not exposed through `uiApi.mjs` even though native `IExtensionUi` has several of them.
3. **Async-vs-sync divergence**: `getActiveTools`/`getAllTools`/`getThinkingLevel`/`getFlag`/`sendMessage` are async in the PiSharp bridge vs sync in JS pi types (`pisharp-js-pi-extension-parity-gaps.md` §Root pi API gaps).
4. **Package list object-form filters** not implemented; `update self` parsed but reports not implemented (`docs\analysis\ANALYSIS-epic-12-js-extension-parity.md`; `docs\pisharp-tools.md` §Package CLI).
5. **No `PiSharp.Client` / daemon** in main — the entire daemon-client architecture is design-only + worktree (see §6.2, §11).
6. **Server is disconnected**: `PiSharp.Server` exists with the 17-command WS protocol but nothing connects to it (`docs\plans\2026-08-14-daemon-client-architecture-design.md` Context).
7. **Native-vs-JS extension ergonomics** (inventory doc §Native C# API gaps): `IExtensionApi` has no `GetCommands`; `IExtensionSessionApi` has no `NewSessionAsync`/`WaitForIdleAsync`/`ForkAsync`/`SwitchSessionAsync`/`NavigateTreeAsync` (delegates exist on `ExtensionRuntimeBinding` only) — see §7.5.
8. **Keybindings file unread**: `~/.pi/agent/keybindings.json` path exists in `PiAgentPaths` but the TUI does not load it (§9).
9. **Structured logging plan not implemented**: file logging is plain-text rolling logs (`src\PiSharp.Cli\Logging\`), while `docs\plans\2026-06-01-structured-logging-plan.md` exists (§8).
10. **RPC `abort_bash` no-op** until a long-lived bash runner exists (`docs\pisharp-tools.md` §File and shell behavior).
11. **Coordination limits** (doc-stated, `docs\pisharp-agent-coordination.md` §Known limits): `isolated: true` / `extensions: false` subagents invisible; single-repo scope; in-process daemon lost when the owning agent exits; no delivery guarantees beyond JSONL replay; cross-platform pipe behavior unverified.

---

*Sources: `G:\code\AI\pi\PiSharp` main branch, inspected 2026-08-14. Guided-map docs: README.md, docs\pisharp-developer-guide.md, docs\pisharp-runtime.md, docs\pisharp-tools.md, docs\pisharp-typescript-extensions.md, docs\pisharp-native-extensions.md, docs\pisharp-agent-coordination.md, docs\pisharp-vs-pi.md, docs\analysis\pisharp-extension-api-inventory.md, docs\analysis\pisharp-js-pi-extension-parity-gaps.md, docs\analysis\ANALYSIS-epic-12-js-extension-parity.md, docs\analysis\js-pi-extension-api-inventory.md, docs\plans\2026-08-14-daemon-client-architecture-design.md. Source verification: all `src\` projects listed in §1, `tests\` file names, `extensions\` sources, and the daemon-client worktree diff.*
