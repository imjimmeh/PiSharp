# PiSharp Non-Web Architecture Review

**Date:** 2026-05-29  
**Scope:** PiSharp TUI/CLI and related non-web packages.  
**Excluded:** `src/PiSharp.Server`, `src/pisharp-session-webapp`, API/web app surfaces.

## Reviewed Areas

Primary source packages reviewed:

- `src/PiSharp.Cli`
- `src/PiSharp.Tui`
- `src/PiSharp.Agent`
- `src/PiSharp.Agent.Core`
- `src/PiSharp.Runtime`
- `src/PiSharp.Tools`
- `src/PiSharp.Extensions`
- `src/PiSharp.PluginHost`
- `src/PiSharp.TsBridge`
- `src/PiSharp.Ai`
- `src/PiSharp.Compatibility`
- related tests under `tests/PiSharp.*.Tests`

Relevant docs reviewed:

- `docs/specs/SDD-pi-csharp-port.md`
- `docs/epics/EPIC-05-cli-and-modes.md`
- `docs/epics/EPIC-07-agent-harness-event-pipeline-refactor.md`
- `docs/epics/EPIC-09-interactive-tui-visual-parity.md`
- `docs/specs/TUI-visual-parity-contract.md`

## Architectural Vocabulary Used

- **Module** — anything with an interface and implementation: function, class, package, subsystem.
- **Interface** — everything a caller must know to use the module: types, invariants, ordering, error modes, config.
- **Implementation** — code inside the module.
- **Depth** — leverage at the interface. A deep module hides much behavior behind a small interface.
- **Shallow module** — module whose interface is nearly as complex as its implementation.
- **Seam** — where an interface lives; a place behavior can change without editing in place.
- **Adapter** — concrete thing satisfying an interface at a seam.
- **Leverage** — what callers gain from depth.
- **Locality** — what maintainers gain when behavior, bugs, and knowledge are concentrated.

## Executive Summary

PiSharp has a strong foundation. The most important architectural choice is already correct: the system is event-driven at the agent core. `AgentLoop` emits events, `AgentHarness` wraps and persists them, extensions receive mapped events, and the TUI reduces them into render state. This is a high-leverage spine.

The current main risk is not absence of architecture. The risk is that several central modules have become orchestration magnets. They still work, but they attract every new feature:

1. `TuiHost.cs` is the largest and most urgent refactoring target.
2. `TuiRenderState.cs` is a good immutable model but is accumulating too many reducer responsibilities.
3. `PiRuntimeBootstrap.cs` is a broad startup orchestrator that should become a startup pipeline.
4. `SessionRuntime.cs` is a useful facade but mixes session binding, extension binding, model persistence, and lifecycle.
5. `ExtensionRegistry.cs` has good ownership semantics, but its change publication should become a first-class event stream.
6. Tools have good interfaces but duplicate filesystem search/glob/fallback behavior.

The codebase is good enough to improve incrementally. The best path is not a rewrite. It is a sequence of deepening refactors that turn shallow orchestration modules into small composition roots plus deeper modules behind explicit seams.

---

# What Is Good So Far

## 1. Strong Event-Driven Spine

The design goal in `docs/specs/SDD-pi-csharp-port.md` explicitly calls out event-driven architecture using `IAsyncEnumerable<T>` and event streams. The implementation largely follows that intent.

Important files:

- `src/PiSharp.Agent/Loops/AgentLoop.cs`
- `src/PiSharp.Agent/Harness/AgentHarness.cs`
- `src/PiSharp.Agent.Core/Events/AgentHarnessEvent.cs`
- `src/PiSharp.Extensions/ExtensionEvents.cs`
- `src/PiSharp.Tui/Interactive/TuiRenderState.cs`

Observed event path:

1. `AgentLoop` emits `AgentEvent` values while executing a turn.
2. `AgentHarness` receives these events through `HandleLoopEventAsync`.
3. `AgentHarness` wraps core events as `AgentHarnessEvent.Core`.
4. Harness event pipeline persists messages, updates phase, dispatches to extensions, and notifies listeners.
5. TUI subscriptions reduce events into `TuiRenderState`.
6. Extensions receive mapped event names through `ExtensionEventMapper` and `ExtensionRegistry.DispatchAsync`.

This is a very good core shape. It gives PiSharp a natural way to support:

- streaming UI updates
- extension hooks
- session durability
- tool execution state
- steering/follow-up messages
- telemetry/observability later

## 2. Good Existing Seams

Several seams are already strong:

### Execution environment seam

Files:

- `src/PiSharp.Abstractions/Environment/*`
- `src/PiSharp.Runtime/IO/SystemExecutionEnv.cs`
- `tests/PiSharp.Tools.Tests/Fakes/FakeExecutionEnv.cs`

The tools depend on `IExecutionEnv`, not directly on `System.IO` or process APIs. This is excellent for testability.

### Tool interface seam

Files:

- `src/PiSharp.Agent.Core/Tools/*`
- `src/PiSharp.Tools/JsonTool.cs`
- `src/PiSharp.Tools/ToolSchemas.cs`

`JsonTool<TParameters,TDetails>` is a good deep module. Tool implementations can expose typed input/output while still presenting JSON schema to the model/runtime.

### Extension registry seam

Files:

- `src/PiSharp.Extensions/ExtensionRegistry.cs`
- `src/PiSharp.Extensions/ExtensionManager.cs`
- `src/PiSharp.Extensions/ExtensionRuntimeBinding.cs`

`ExtensionRegistry` tracks source ownership and override policy. This is important because extension systems easily become unmanageable without ownership.

### Runtime facade seam

File:

- `src/PiSharp.Runtime/Runtime/SessionRuntime.cs`

`SessionRuntime` gives CLI modes a unified runtime object. That is good. The module now needs internal deepening, not removal.

## 3. TUI Testing Ambition Is Strong

Files:

- `tests/PiSharp.Tui.Tests/TuiRenderingTests.cs`
- `tests/PiSharp.Tui.Tests/TuiParitySnapshotTests.cs`
- `tests/PiSharp.Tui.Tests/TuiShortcutTests.cs`
- `tests/PiSharp.Tui.Tests/TuiRenderStateTests.cs`
- `tests/PiSharp.Tui.Tests/TuiRenderSchedulerTests.cs`

The TUI has unusually good test coverage intent for terminal UI code. Snapshot parity with the JavaScript baseline is especially valuable.

`docs/specs/TUI-visual-parity-contract.md` is also a strong artifact because it makes visual parity testable instead of subjective.

## 4. The Harness Event Pipeline Is the Right Direction

Files:

- `src/PiSharp.Agent/Harness/LoopEvents/*`
- `docs/epics/EPIC-07-agent-harness-event-pipeline-refactor.md`

The code now contains a pipeline with stages:

- `PersistenceStage`
- `PhaseTransitionStage`
- `ExtensionDispatchStage`
- `ListenerNotificationStage`

This is exactly the kind of deep module the codebase needs more of: explicit ordering, isolated concerns, testable behavior.

---

# High-Priority Refactoring Opportunities

## 1. Deepen the TUI Host into an Event-Driven Application Shell

### Files

- `src/PiSharp.Tui/Interactive/TuiHost.cs`
- `src/PiSharp.Tui/Interactive/TuiRenderState.cs`
- `src/PiSharp.Tui/Interactive/TuiRenderScheduler.cs`
- `src/PiSharp.Tui/Interactive/TuiRenderRequestRouter.cs`
- `src/PiSharp.Tui/Interactive/Components/*`

### Current State

`TuiHost.cs` is roughly 1050 lines. It is the clearest architecture hotspot in the reviewed non-web code.

It currently owns:

- Terminal.Gui initialization
- terminal screen lifecycle
- window/view construction
- layout calculations
- render scheduling
- app resize handling
- render state mutation
- harness subscription lifecycle
- command dispatch
- inline selection
- prompt submission
- prompt history behavior glue
- session snapshot refresh
- session fork from transcript rows
- chat context menu handling
- clipboard feedback
- extension UI bridge creation
- extension shortcut parsing/conflict reporting
- built-in shortcut dispatch
- working indicator animation
- footer/header rendering
- prompt focus management

This is too much for one module. It is currently shallow because using or modifying it requires understanding many unrelated workflows.

### Why This Causes Friction

Adding any TUI feature requires touching `TuiHost.cs`, even if the feature is conceptually narrow. Examples:

- new shortcut behavior
- new command lifecycle behavior
- new extension UI intent
- new session-switch edge case
- new render scheduling rule
- new layout behavior

The module has low locality: bugs in command handling, extension UI, layout, and session switching all converge in one file.

### Recommended Refactor

Keep `TuiHost` as a composition root, but extract deep modules.

Suggested modules:

#### `TuiAppController`

Owns application-level state transitions:

- current `TuiRenderState`
- current harness reference
- session snapshot apply/refresh
- submit prompt
- abort
- append system message

#### `TuiLayoutComposer`

Owns Terminal.Gui construction and layout:

- create window/header/chat/prompt/footer/suggestions
- apply layout metrics
- calculate bottom reserved height
- place views

#### `TuiCommandController`

Owns slash command and inline selection lifecycle:

- `TryHandleCommandAsync`
- command busy flag
- `/help`, `/hotkeys`, `/clear`, `/abort`, `/exit`
- dispatch to `TuiCommandDispatchRequest`
- inline selection begin/complete/cancel

#### `TuiShortcutController`

Owns shortcut registration and dispatch:

- built-in shortcut context
- extension shortcut parsing
- conflict detection
- shortcut error reporting

#### `TuiExtensionUiAdapter`

Move `TuiExtensionUi` out of `TuiHost.cs` and make it a real adapter from `IExtensionUi` to `ExtensionUiBridgeHost`.

#### `TuiRenderLoop`

Owns:

- `TuiRenderScheduler`
- animation timer
- resize events
- render request coalescing

### Benefits

- **Locality:** command bugs live in command controller; layout bugs live in layout composer.
- **Leverage:** `TuiHost` becomes a small shell that composes deep modules.
- **Testability:** command/shortcut/session controllers can be tested without Terminal.Gui full app lifecycle.
- **SOLID:** fewer reasons for `TuiHost` to change.

### Suggested First Commit

Move only `TuiExtensionUi` into its own file:

- `src/PiSharp.Tui/Interactive/TuiExtensionUi.cs`

This is low-risk and starts shrinking `TuiHost.cs`.

### Suggested Follow-Up Commits

1. Extract `TuiExtensionUi`.
2. Extract `TuiCommandController` around `TryHandleCommandAsync` and inline selection.
3. Extract `TuiShortcutController`.
4. Extract layout composer.
5. Extract render loop/animation.

---

## 2. Split `TuiRenderState` into Smaller Reducers

### Files

- `src/PiSharp.Tui/Interactive/TuiRenderState.cs`
- `tests/PiSharp.Tui.Tests/TuiRenderStateTests.cs`

### Current State

`TuiRenderState` is an immutable record, which is good. It also centralizes a large amount of behavior:

- append system rows
- pinned/unpinned local row restoration
- clear transcript
- toggle thinking/tool output
- toggle tool expansion
- cache rendered tool lines
- update editor text/title/working indicator
- bridge slot upsert/remove
- extension status update
- session hydration
- preserve tool UI state across hydration
- find transcript by entry id
- reduce `AgentHarnessEvent`
- upsert messages and tool calls
- text extraction from message content
- tool result text formatting

The data model is becoming the reducer for every TUI concept.

### Why This Causes Friction

Adding a new visual state or event kind means editing `TuiRenderState.cs`. That makes the module a central churn point.

The interface also grows with unrelated methods. Callers who only need bridge slot behavior still see transcript/session/tool APIs.

### Recommended Refactor

Keep `TuiRenderState` as data. Move behavior into reducer modules:

- `TranscriptReducer`
- `ToolTranscriptReducer`
- `SessionHydrationReducer`
- `BridgeSlotReducer`
- `HarnessEventReducer`
- `WorkingIndicatorReducer`

Possible shape:

```csharp
public static class TuiStateReducer
{
    public static TuiRenderState Reduce(TuiRenderState state, AgentHarnessEvent evt)
        => HarnessEventReducer.Reduce(state, evt);
}
```

Or:

```csharp
public interface ITuiReducer<TEvent>
{
    TuiRenderState Reduce(TuiRenderState state, TEvent evt);
}
```

### Benefits

- **Locality:** tool transcript state is isolated from session hydration.
- **Leverage:** reducers can be tested directly and reused by replay/snapshot tools.
- **Open/closed:** adding a new event category does not require growing one central record.

### Priority

High. This should happen after or alongside `TuiHost` decomposition.

---

## 3. Make Extension Registry Changes a First-Class Event Stream

### Files

- `src/PiSharp.Extensions/ExtensionRegistry.cs`
- `src/PiSharp.Runtime/Runtime/SessionRuntime.cs`
- `src/PiSharp.TsBridge/TsExtensionHost.cs`
- `src/PiSharp.Tui/Interactive/TuiHost.cs`

### Current State

`ExtensionRegistry` has strong ownership and override semantics. It tracks tools, providers, commands, shortcuts, flags, renderers, decorators, prompt contributors, prompt sections, and prompt transforms.

It also exposes:

```csharp
public event Func<ExtensionRegistryChange, CancellationToken, Task>? Changed;
```

But publishing is fire-and-forget:

```csharp
private void Publish(ExtensionRegistryChange change)
{
    var handlers = Changed;
    if (handlers is null) return;
    _ = handlers(change, CancellationToken.None);
}
```

This means registry changes are event-like, but delivery semantics are weak. Failures, ordering, and completion are not explicit.

### Why This Causes Friction

Extension registration affects many runtime surfaces:

- harness active tools
- TUI extension shortcuts
- prompt contributors
- provider registry
- TypeScript descriptor replay
- reload/unload behavior

A weak event seam risks subtle bugs during hot reload, descriptor replay, or extension unload.

### Recommended Refactor

Create a dedicated change stream module.

Possible names:

- `ExtensionRegistryChangeBus`
- `ExtensionRegistryEventStream`
- `ExtensionRegistryChangeDispatcher`

Possible interface:

```csharp
public interface IExtensionRegistryChangeStream
{
    IDisposable Subscribe(Func<ExtensionRegistryChange, CancellationToken, Task> handler);
    Task PublishAsync(ExtensionRegistryChange change, CancellationToken cancellationToken);
}
```

Alternative: make mutation methods return changes and let a coordinator publish them:

```csharp
public IReadOnlyList<ExtensionRegistryChange> RegisterTool(...);
```

Then runtime adapters consume changes:

- `HarnessToolRegistryAdapter`
- `TuiShortcutRegistryAdapter`
- `ProviderRegistryAdapter`
- `PromptContributorRegistryAdapter`

### Benefits

- **Locality:** propagation policy is isolated.
- **Leverage:** all registry consumers get consistent ordering/fault behavior.
- **Event-driven:** extension changes become part of the same architectural style as agent events.
- **Testability:** registry change side effects can be tested without full runtime.

### Priority

High.

---

## 4. Split Runtime Bootstrap into Startup Phases

### Files

- `src/PiSharp.Runtime/Runtime/PiRuntimeBootstrap.cs`
- `tests/PiSharp.Runtime.Tests/Runtime/PiRuntimeBootstrapTests.cs`

### Current State

`PiRuntimeBootstrap.CreateRuntimeAsync` is broad. It handles:

- settings load
- credential resolver creation
- provider registration
- resource loading
- prompt template loading
- theme loading
- session root/repo/session resolution
- built-in tool resolution
- extension registry/manager creation
- extension runtime binding setup
- native extension discovery/loading
- TypeScript bridge startup
- TypeScript descriptor replay
- eager TypeScript extension loading
- extension flag application
- model selection
- system prompt construction
- skill loading
- harness factory creation
- final `SessionRuntime` assembly
- startup benchmark instrumentation

The method has good benchmarking, but it is doing too much.

### Why This Causes Friction

Startup is likely to keep changing:

- extension startup performance
- model registry behavior
- theme loading
- prompt templates
- compatibility mode
- startup diagnostics
- CLI flags

With all of this in one method, small changes risk accidental interactions.

### Recommended Refactor

Create a startup pipeline with typed phase outputs.

Suggested phases:

- `SettingsStartupPhase`
- `ProviderStartupPhase`
- `ResourceStartupPhase`
- `PromptTemplateStartupPhase`
- `ThemeStartupPhase`
- `SessionStartupPhase`
- `ToolStartupPhase`
- `ExtensionStartupPhase`
- `ModelStartupPhase`
- `PromptStartupPhase`
- `SkillStartupPhase`
- `RuntimeAssemblyPhase`

Shared context:

```csharp
public sealed record RuntimeStartupContext(
    PiRuntimeOptions Options,
    StartupBenchmarkCollector? Benchmark,
    List<RuntimeDiagnostic> Diagnostics,
    ...);
```

Each phase can have:

```csharp
public interface IRuntimeStartupPhase
{
    string Name { get; }
    Task ExecuteAsync(RuntimeStartupContext context, CancellationToken cancellationToken);
}
```

### Benefits

- **Locality:** extension startup logic lives in one phase.
- **Leverage:** benchmark/diagnostics become reusable phase concerns.
- **Testability:** phases can be tested independently.
- **SOLID:** startup grows by adding phases instead of editing a long method.

### Priority

High.

---

## 5. Split `SessionRuntime` into Runtime Controllers

### Files

- `src/PiSharp.Runtime/Runtime/SessionRuntime.cs`
- `src/PiSharp.Cli/Modes/InteractiveMode.cs`
- `src/PiSharp.Cli/Modes/RpcMode.cs`

### Current State

`SessionRuntime` is a valuable facade, but it currently owns:

- current session
- current harness
- extension manager
- plugin host
- TS host
- settings store/snapshot
- current model selection
- resources/theme/skills/prompt templates
- extension binding
- extension flag diagnostics
- startup benchmark
- extension load coordinator
- harness event forwarding
- extension runtime binding
- extension message routing
- registry change handling
- extension reload
- model/thinking persistence
- new/switch/fork session
- session replacement/rebinding
- disposal

### Why This Causes Friction

`SessionRuntime` has too many reasons to change:

- session switching bug
- model persistence bug
- extension hot reload bug
- TS event forwarding bug
- runtime disposal bug

All of these land in the same module.

### Recommended Refactor

Keep `SessionRuntime` as a facade, but extract controllers:

#### `RuntimeSessionController`

Owns:

- `NewSessionAsync`
- `SwitchSessionAsync`
- `ForkAsync`
- `ReplaceSessionAsync`
- session snapshot/rebind mechanics

#### `RuntimeExtensionBinder`

Owns:

- `BindExtensionRuntime`
- registry change application
- harness tool registration
- TS forwarding subscription
- extension runtime action wiring

#### `RuntimeModelController`

Owns:

- `SetModelAsync`
- `SetThinkingLevelAsync`
- settings persistence

#### `RuntimeExtensionReloader`

Owns:

- unload/reload lifecycle
- extension load coordinator invalidation
- preserving extension state assumptions

### Benefits

- **Locality:** session switching can be reasoned about independently.
- **Leverage:** CLI/RPC/TUI continue using `SessionRuntime` without knowing internals.
- **SOLID:** fewer responsibilities per module.

### Priority

High.

---

# Medium-High Priority Opportunities

## 6. Finish Harness Event Pipeline Migration for Own-Events and Middleware

### Files

- `src/PiSharp.Agent/Harness/AgentHarness.cs`
- `src/PiSharp.Agent/Harness/LoopEvents/*`
- `docs/epics/EPIC-07-agent-harness-event-pipeline-refactor.md`

### Current State

Core loop events use `LoopEventPipeline`:

```csharp
_loopEventPipeline = new LoopEventPipeline([
    new PersistenceStage(),
    new PhaseTransitionStage(),
    new ExtensionDispatchStage(),
    new ListenerNotificationStage()
]);
```

But own-events still use bespoke paths:

- `PublishOwnEventAsync`
- `DispatchBeforeAgentStartAsync`
- `RunBeforeToolMiddlewareAsync`
- `RunAfterToolMiddlewareAsync`

This means event policy is split.

### Recommended Refactor

Generalize the pipeline so it can process all `AgentHarnessEvent` values, not only core loop events.

Possible shape:

```csharp
public sealed record HarnessEventContext(
    AgentHarnessEvent Event,
    HarnessEventKind Kind,
    ...);
```

Stages decide whether they apply:

- persistence stage: mostly core events
- phase stage: core and selected own-events
- extension dispatch stage: all extension-visible events
- listener stage: all events
- middleware stage: before/after tool events

### Benefits

- **Locality:** one event policy path.
- **Leverage:** future observer policies are added once.
- **Consistency:** own-events get same fault/cancellation semantics as core events.

### Priority

Medium-high.

---

## 7. Extract Shared Search Backend for Tools

### Files

- `src/PiSharp.Tools/Search/GrepTool.cs`
- `src/PiSharp.Tools/Search/FindTool.cs`
- `src/PiSharp.Tools/Search/LsTool.cs`
- `src/PiSharp.Tools/Shared/PathUtilities.cs`
- `src/PiSharp.Tools/Shared/Truncation.cs`

### Current State

`FindTool` and `GrepTool` duplicate several concepts:

- external command fallback (`fd`, `rg`)
- native recursive enumeration
- glob-to-regex conversion
- relative path formatting
- result limiting
- truncation messaging

### Why This Causes Friction

Search behavior is core to coding-agent quality. Small inconsistencies matter:

- glob semantics differ between find/grep
- fallback behavior differs between systems
- quoting issues can be repeated
- `.gitignore`/hidden file behavior may drift

### Recommended Refactor

Create deeper search modules:

```csharp
public interface IFileSearchBackend
{
    Task<FileSearchResult> FindAsync(FileSearchRequest request, CancellationToken cancellationToken);
}

public interface IContentSearchBackend
{
    Task<ContentSearchResult> SearchAsync(ContentSearchRequest request, CancellationToken cancellationToken);
}
```

Adapters:

- `FdFileSearchBackend`
- `NativeFileSearchBackend`
- `RipgrepContentSearchBackend`
- `NativeContentSearchBackend`

Shared modules:

- `GlobMatcher`
- `RelativePathFormatter`
- `SearchResultFormatter`
- `ExternalSearchCommandBuilder`

### Benefits

- **DRY:** shared glob/path/search behavior.
- **Locality:** fallback logic tested once.
- **Leverage:** tools become thin interfaces over deep behavior.
- **Quality:** easier to add `.gitignore`, hidden file, symlink, and binary file policy consistently.

### Priority

Medium-high.

---

## 8. Provider Streaming/Conversion Pipeline

### Files

- `src/PiSharp.Ai/Providers/OpenAI/OpenAIResponsesProvider.cs`
- `src/PiSharp.Ai/Providers/OpenAI/OpenAICompletionsProvider.cs`
- `src/PiSharp.Ai/Providers/Anthropic/AnthropicProvider.cs`
- `src/PiSharp.Ai/Providers/Google/*`
- `src/PiSharp.Ai/Providers/Shared/ProviderHttp.cs`

### Current State

`ProviderHttp` is a good shared seam. Provider classes still tend to own several behaviors:

- request mapping
- message conversion
- stream parsing
- tool call conversion
- usage conversion
- error normalization

Provider complexity is unavoidable, but it should be pushed behind deep provider-specific adapters.

### Recommended Refactor

Introduce provider pipeline parts:

- `IProviderRequestMapper`
- `IProviderStreamParser`
- `IProviderErrorMapper`
- `ProviderStreamNormalizer`
- `ToolCallDeltaAssembler`
- `UsageNormalizer`

Provider classes become thin composition roots:

```csharp
public sealed class AnthropicProvider : IModelProvider
{
    public IAsyncEnumerable<AssistantMessageEvent> StreamAsync(...)
        => _pipeline.StreamAsync(...);
}
```

### Benefits

- **Locality:** API quirks stay near provider adapters.
- **Leverage:** agent loop consumes one normalized event stream.
- **Testability:** stream parsers can be tested with fixture event chunks.

### Priority

Medium-high.

---

# Medium Priority Opportunities

## 9. Decouple CLI Entry Point Helpers

### Files

- `src/PiSharp.Cli/Program.cs`
- `src/PiSharp.Cli/Bootstrap/CliRuntimeOptionsMapper.cs`
- `src/PiSharp.Cli/Runtime/StartupResourceSummary.cs`

### Current State

`Program.cs` is not the worst offender, but it includes helper modules inline:

- `StartupResumeSelector`
- `CliHelp`
- `StartupBenchmarkFormatter`

### Recommended Refactor

Move them to dedicated files:

- `Bootstrap/StartupResumeSelector.cs`
- `Parsing/CliHelpRenderer.cs`
- `Runtime/StartupBenchmarkFormatter.cs`

### Benefits

- **Locality:** `Program` becomes parse/bootstrap/mode dispatch only.
- **Testability:** help and benchmark formatting can be tested directly.
- **Low risk:** mechanical cleanup.

### Priority

Medium.

---

## 10. Avoid Rebuilding Slash Command Registry Repeatedly

### Files

- `src/PiSharp.Cli/Modes/InteractiveMode.cs`
- `src/PiSharp.Cli/Commands/*`

### Current State

`InteractiveMode.CreateTuiHostOptions` builds the command registry inside delegates:

```csharp
var result = await BuildCommandRegistry(runtime).ExecuteAsync(...)
text => BuildCommandRegistry(runtime).Complete(text)
```

This may be acceptable if cheap, but it means command registry construction is not clearly a module with lifecycle.

### Recommended Refactor

Create `SlashCommandRegistryProvider` or build once and update on extension registry changes.

```csharp
var commandRegistry = SlashCommandRegistryFactory.Create(runtime);
```

If commands are dynamic due to extension reload, connect it to the extension registry change stream.

### Benefits

- **Locality:** command registration policy is explicit.
- **Leverage:** completion and execution share the same registry instance.
- **Performance:** avoids repeated registry construction.

### Priority

Medium.

---

## 11. Model Catalog Policy Seam

### Files

- `src/PiSharp.Ai/Models/Generated/BuiltInModels.g.cs`
- `src/PiSharp.Ai/Models/Generation/ModelCatalogGenerator.cs`
- `src/PiSharp.Ai/Models/ModelRegistry.cs`
- `src/PiSharp.Runtime/Runtime/RuntimeModelSelector.cs`

### Current State

`BuiltInModels.g.cs` is very large, which is fine because it is generated. The key risk is allowing behavior to leak into generated data shape.

### Recommended Refactor

Ensure generated models stay dumb and behavior lives in named policy modules:

- `ModelCatalog`
- `ModelSelectionPolicy`
- `ThinkingLevelPolicy`
- `ProviderCapabilityIndex`

### Benefits

- **Locality:** generation changes do not affect selection policy.
- **Leverage:** model selection rules can be tested and evolved independently.

### Priority

Medium.

---

# Package-Specific Notes

## `PiSharp.Tui`

### Strengths

- Immutable `TuiRenderState` is a good foundation.
- Snapshot parity contract exists.
- `TuiRenderScheduler` is small and focused.
- Components like `FooterView`, `HeaderView`, `ChatView`, `PromptEditor`, `ToolExecutionView` show a componentized direction.
- `TuiLayoutMetrics` suggests layout logic is already being named.

### Weak Spots

- `TuiHost` remains a large procedural shell.
- `ChatView` is also sizable and owns rendering cache, scrolling, selection, mouse interaction, context targets, and clipboard copy.
- `TuiRenderState` mixes data and multiple reducers.

### Further Opportunities

`ChatView` could eventually split into:

- `TranscriptRowCache`
- `ChatSelectionController`
- `ChatScrollController`
- `ChatInteractionHitTester`
- `ChatRowComposer`

This is lower priority than `TuiHost`, but likely worthwhile later.

## `PiSharp.Cli`

### Strengths

- `IConsoleIO` seam supports tests.
- Modes are separated: `InteractiveMode`, `PrintMode`, `RpcMode`.
- `CliRuntimeOptionsMapper` keeps parsing separate from runtime options.
- Slash command registry is a useful module.

### Weak Spots

- Some helper modules live inside `Program.cs`.
- `InteractiveMode` couples command registry building to TUI option creation.
- CLI help text and parser must stay synchronized manually.

### Further Opportunities

Generate help from parser metadata if possible, or centralize flag definitions so parse/help/runtime mapping cannot drift.

## `PiSharp.Agent` / `PiSharp.Agent.Core`

### Strengths

- `AgentLoop` is a pure-ish loop module with event emission.
- Harness event pipeline is the right direction.
- Session abstractions are separated from loop execution.
- Compaction and branch summarization have named modules.

### Weak Spots

- `AgentHarness` still owns many concerns: queues, tools, model/thinking, prompt building, middleware, event handling, compaction, navigation.
- Own-events and middleware bypass the new pipeline.

### Further Opportunities

Extract from `AgentHarness`:

- `HarnessQueueController`
- `HarnessToolRegistry`
- `HarnessPromptBuilder`
- `HarnessCompactionController`
- `HarnessTreeNavigationController`
- `HarnessEventPublisher`

Do this carefully; `AgentHarness` is central and heavily tested.

## `PiSharp.Runtime`

### Strengths

- `SessionRuntime` is a useful facade.
- Startup benchmark instrumentation is thoughtful.
- Runtime options mapping isolates CLI args from runtime creation.

### Weak Spots

- `PiRuntimeBootstrap` is too broad.
- `SessionRuntime` mixes lifecycle, session, model, extension, TS bridge, and persistence concerns.

### Further Opportunities

Make runtime startup and runtime session control explicit pipelines/controllers.

## `PiSharp.Extensions`

### Strengths

- Source ownership is tracked.
- Override policies exist.
- Registry supports many extension surfaces.
- `ExtensionManager.Unload` attempts to restore provider registry winners.

### Weak Spots

- Registry change publication is fire-and-forget.
- Registry has many dictionaries and repeated registration patterns.
- Event bus API has `EmitAsync` as no-op, which may be surprising.

### Further Opportunities

Use typed registration category modules internally:

- `ExtensionRegistrationTable<T>`
- `ExtensionRegistrationStack<T>`
- `ExtensionRegistrationCategory<T>`

This would reduce repeated dictionary/set/push/remove logic.

## `PiSharp.TsBridge`

### Strengths

- Descriptor replay is a good performance optimization.
- Lazy activation of descriptor-backed extensions is a strong design.
- TypeScript bridge isolates compatibility concerns from core runtime.

### Weak Spots

`TsExtensionHost` is broad. It owns:

- process start
- JSON-RPC connection
- stderr buffering
- load many
- descriptor cache persistence
- descriptor validation/hash checking
- descriptor registration
- event forwarding
- UI bridge
- provider adapter registration
- command/shortcut activation wrappers

### Further Opportunities

Extract:

- `TsBridgeProcess`
- `TsDescriptorCache`
- `TsDescriptorRegistrar`
- `TsExtensionActivator`
- `TsEventForwarder`
- `TsUiBridgeAdapter`

Priority is lower than TUI/runtime, but it will matter as TS extension compatibility grows.

## `PiSharp.Tools`

### Strengths

- Tool implementations are concise.
- `JsonTool<TParameters,TDetails>` is a strong base.
- `OutputAccumulator` is a deep module for streaming/truncated output.
- `Truncation` behavior appears centralized.

### Weak Spots

- Search tools duplicate path/glob/enumeration behavior.
- External command quoting is local to tools.
- Native fallback behavior may drift from external tool behavior.

### Further Opportunities

Introduce search backend modules as described above.

---

# Suggested Refactoring Roadmap

## Phase 1: Low-Risk Shrinkage

Goal: reduce obvious large files without changing behavior.

1. Move `TuiExtensionUi` to its own file.
2. Move `StartupResumeSelector`, `CliHelp`, `StartupBenchmarkFormatter` out of `Program.cs`.
3. Move descriptor cache logic out of `TsExtensionHost` into `TsDescriptorCache`.
4. Add tests around moved modules before/after if not already covered.

## Phase 2: TUI Deepening

Goal: make TUI maintainable before more visual parity work lands.

1. Extract `TuiCommandController`.
2. Extract `TuiShortcutController`.
3. Extract `TuiLayoutComposer`.
4. Extract `TuiRenderLoop`.
5. Split `TuiRenderState` reducers.

Success criteria:

- `TuiHost.cs` becomes mostly composition and lifecycle.
- Command behavior can be tested without full Terminal.Gui run loop.
- Snapshot tests still pass.

## Phase 3: Runtime/Extension Deepening

Goal: make dynamic extension/session behavior reliable.

1. Replace fire-and-forget registry change publication with explicit change stream.
2. Extract `RuntimeExtensionBinder`.
3. Extract `RuntimeSessionController`.
4. Extract `RuntimeModelController`.
5. Convert `PiRuntimeBootstrap` to startup phases.

Success criteria:

- Extension reload behavior is testable in isolation.
- Session replacement has focused tests.
- Startup benchmark phases remain intact or improve.

## Phase 4: Core Event Pipeline Completion

Goal: unify event policy.

1. Generalize harness event pipeline for own-events.
2. Move before-agent-start handling into pipeline/event publisher.
3. Move before/after tool middleware policy into pipeline-compatible modules.
4. Add ordering/fault-policy tests.

Success criteria:

- One harness event delivery policy.
- Extension/listener fault isolation test-locked.
- Durability-first behavior remains preserved.

## Phase 5: Tool and Provider Polish

Goal: reduce duplication and make provider/tool behavior more consistent.

1. Introduce file/content search backends.
2. Centralize glob/path/result formatting.
3. Introduce provider stream parser/request mapper seams.
4. Add fixture tests for provider streaming conversions.

---

# Top 10 Refactor Candidates, Ranked

1. **TUI host decomposition** — highest immediate payoff.
2. **TUI reducer decomposition** — prevents state model from becoming a god object.
3. **Extension registry change stream** — improves event-driven consistency and hot reload reliability.
4. **Runtime bootstrap phases** — improves startup maintainability and diagnostics.
5. **SessionRuntime controller split** — improves session/model/extension locality.
6. **Harness own-event pipeline completion** — unifies event policy.
7. **Search backend extraction for tools** — DRY + consistent behavior.
8. **Provider streaming/conversion pipeline** — better provider maintainability.
9. **CLI helper extraction and command registry lifecycle** — low-risk cleanup.
10. **Model catalog policy seam** — protects generated data from behavior coupling.

---

# Concrete First Refactor Recommendation

Start with `TuiHost` because it is the biggest source of future drag.

Recommended first issue/PR:

## PR 1: Extract TUI Extension UI Adapter

Files:

- Create `src/PiSharp.Tui/Interactive/TuiExtensionUi.cs`
- Remove `TuiExtensionUi` from `TuiHost.cs`
- No behavior changes
- Run `tests/PiSharp.Tui.Tests`

Why first:

- Very low risk.
- Immediately shrinks the largest file.
- Establishes the pattern: `TuiHost` composes adapters, does not contain them.

## PR 2: Extract TUI Command Controller

Move command-related behavior from `TuiHost`:

- command busy state
- built-in slash command handling
- dispatch command callback
- inline selection session
- resume/session refresh after commands

Expected new file:

- `src/PiSharp.Tui/Interactive/TuiCommandController.cs`

Potential interface:

```csharp
public sealed class TuiCommandController
{
    public Task<bool> TryHandleCommandAsync(string text, CancellationToken cancellationToken);
    public Task<string?> SelectInlineAsync(string title, IReadOnlyList<string> choices, CancellationToken cancellationToken);
    public void CancelInlineSelection();
}
```

## PR 3: Extract TUI Shortcut Controller

Move:

- extension shortcut binding construction
- built-in shortcut context
- conflict reporting
- shortcut command dispatch

Expected new file:

- `src/PiSharp.Tui/Interactive/TuiShortcutController.cs`

---

# Final Assessment

PiSharp is in a good place architecturally. It already has the right core instincts:

- event-driven execution
- typed tool contracts
- runtime abstraction
- extension ownership
- testable execution environment
- TUI snapshot parity

The main improvement is to apply the same architectural discipline consistently. The best codebase version of PiSharp is one where:

- `TuiHost` is a small shell, not a giant workflow module.
- `TuiRenderState` is data plus focused reducers, not every reducer at once.
- runtime startup is a phase pipeline.
- extension registry changes are a reliable event stream.
- session/model/extension runtime responsibilities are separated behind controllers.
- tools and providers use deeper shared modules for repeated behaviors.

Do that incrementally and PiSharp can become a very high-quality codebase without a rewrite.
