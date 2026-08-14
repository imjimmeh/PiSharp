# PiSharp Native .NET Extensions

PiSharp native extensions are compiled .NET assemblies loaded from `.dll` files. They run in-process, use PiSharp's C# APIs directly, and can register tools, commands, flags, providers, prompt contributions, event handlers, middleware, and UI integrations.

## Discovery and loading

Native plugins are loaded by `PiSharp.PluginHost.NativePluginHost`.

Discovery includes:

- Explicit `--extension <path>` entries ending in `.dll`.
- Any `.dll` under `~/.pi/extensions`.
- Any `.dll` under `<cwd>/plugins`.
- Any `.dll` under `<cwd>/.pi/extensions`.

Each plugin is loaded into a collectible `PluginLoadContext`. The host validates metadata, finds the first concrete `IExtension` implementation, creates it with `Activator.CreateInstance()`, and initializes it through `ExtensionManager`.

## Install a native DLL

Install a native extension globally with the package command:

```bash
pisharp install path/to/MyExtension.dll
```

This copies the DLL to `~/.pi/extensions/`, which is discovered on later PiSharp starts.

Install a native extension only for the current project with `--local`:

```bash
pisharp install path/to/MyExtension.dll --local
```

This copies the DLL to `<cwd>/.pi/extensions/`. Existing destination DLLs are not replaced unless `--force` is provided.

## Minimal extension

A native extension needs `ExtensionMetadataAttribute` at assembly or type level and at least one concrete `IExtension` implementation.

```csharp
using PiSharp.Extensions;

[assembly: ExtensionMetadata(
    "example.native",
    Name = "Example Native Extension",
    Version = "1.0.0")]

public sealed class ExampleExtension : IExtension
{
    public Task InitializeAsync(IExtensionApi api, CancellationToken cancellationToken = default)
    {
        api.RegisterCommand(new ExtensionCommandRegistration(
            "hello",
            "Say hello",
            async (args, token) =>
            {
                if (api.HasUi)
                    await api.Ui.NotifyAsync($"Hello {args}", ExtensionUiSeverity.Info, token);
            }));

        return Task.CompletedTask;
    }
}
```

## `IExtensionApi`

`IExtensionApi` is the root object passed to `InitializeAsync()`.

| API | Purpose |
| --- | --- |
| `Descriptor` | Extension metadata and source id. |
| `Cwd` | Current working directory. |
| `HasUi` / `Ui` | Access interactive UI only when available. |
| `Session` | Send messages, append custom entries, set session name, set labels. |
| `Tools` | Register tools, inspect all/active tools, set active tools. |
| `Model` | Select model and thinking level. |
| `Events` | Subscribe to and emit extension events. |
| `Prompt` | Register prompt contributors, sections, and transforms. |
| `Settings` | Namespaced settings (`extensions.<ns>.<key>`), `settings_changed` event. |
| `State` | Per-extension versioned key-value state store. |
| `Urls` | Register/unregister `IInternalUrlResolver`s (`skill://`, `agent://`, `diff://`, …). |
| `Completion` | Completion/autocomplete suggestions API (used by the advisor plugin). |
| `ExecutionEnv` | Declarative tool execution environment contracts (shell, env, cwd). |
| `Files` | File/directory access abstractions (used by research + memory plugins). |
| `Search` | Search/retrieval surface. |
| `Packages` | Package lifecycle (`install`/`update`/`remove`/`list` backed by `IPackageCommandRunner`). |
| `Rules` | Rule engine: register `IRuleProvider`s, apply rules with `RuleApplyMode`, list providers. |
| `Telemetry` | Telemetry export surface (OTLP). |

Top-level convenience methods mirror the grouped APIs:

- `On(eventName, handler)`
- `Use(middleware)`
- `RegisterTool(registration)`
- `RegisterCommand(registration)`
- `RegisterShortcut(registration)`
- `RegisterFlag(registration)`
- `RegisterMessageRenderer(registration)`
- `RegisterMessageDecorator(registration)`
- `RegisterProvider(provider)` / `RemoveProvider(api)`
- `GetFlag(name)` / `GetFlags()`
- `SendMessageAsync(message, delivery, triggerTurn)`
- `RegisterSkill(ExtensionSkillDefinition)` / `RegisterRuleProvider(provider)` / `RemoveProvider(api)`
- `EmitClientEventAsync(name, payload, ct)` — publish a custom client event (reserved names map to their dedicated flat event types, e.g. `advisor_note` → `FromAdvisor`)

## Registrations

Native extensions can register:

- Tools via `ExtensionToolRegistration`.
- Slash commands via `ExtensionCommandRegistration`.
- Keyboard shortcuts via `ExtensionShortcutRegistration`.
- CLI flags via `ExtensionFlagRegistration`.
- Message renderers/decorators by row type (`User`, `Assistant`, `ToolCall`, `ToolResult`, `System`, `Error`, `Custom`, `BridgeSlot`). Name-only renderer registrations still compile but are inert compatibility placeholders until a handler is supplied.
- Model providers by implementing `IModelProvider`.
- Prompt contributors, prompt sections, and prompt transforms.

Unknown long CLI flags are captured during CLI parsing and later applied if an extension registers a matching flag.

## Override policy

Registrations use `ExtensionOverridePolicy` where applicable:

| Policy | Meaning |
| --- | --- |
| `Reject` | Default. Reject duplicate registrations. |
| `Override` | Override a previous extension registration. |
| `OverrideBuiltIn` | Explicitly override a built-in registration, such as a built-in tool. |

Use built-in overrides sparingly; they affect model-visible behavior and can surprise users.

## Chat row rendering

Extensions can replace or decorate terminal chat rows without depending on `Terminal.Gui` types. Renderers receive an `ExtensionChatRowRenderContext` and return model-visible/native-safe `ExtensionChatRow` DTOs with text, semantic kind, spans, interaction/context metadata, and layout hints.

```csharp
api.RegisterMessageRenderer(new ExtensionMessageRendererRegistration(
    "compact-tool",
    ExtensionChatRowType.ToolCall,
    context =>
    [
        new ExtensionChatRow(
            $"{context.ToolName}: custom summary",
            context.IsError ? ExtensionChatRowKind.ToolFailed : ExtensionChatRowKind.ToolSucceeded)
    ],
    ExtensionOverridePolicy.OverrideBuiltIn));

api.RegisterMessageDecorator(new ExtensionMessageDecoratorRegistration(
    "assistant-badge",
    ExtensionChatRowType.Assistant,
    (_, rows) => rows.Select(row => row with { Text = "[ext] " + row.Text }).ToArray(),
    Order: 100));
```

The TUI always runs a final safety pass after extension rendering: rows are clipped to the current width, unsafe control sequences are stripped, message context targets are preserved by `ChatView`, and tool toggle targets are restored for tool rows when a replacement renderer omits them.

## Events

Extensions can subscribe to runtime events. Common event names include:

- Session: `session_start`, `session_before_compact`, `session_compact`, `session_before_tree`, `session_tree`, `session_info_changed`
- Agent and turns: `before_prompt_render`, `before_agent_start`, `agent_start`, `agent_end`, `turn_start`, `turn_end`
- Messages: `message_start`, `message_update`, `message_end`
- Tools: `tool_call`, `tool_result`, `tool_execution_start`, `tool_execution_update`, `tool_execution_end`
- Providers/models: `before_provider_request`, `before_provider_payload`, `after_provider_response`, `model_select`, `thinking_level_select`, `thinking_level_changed`
- Runtime: `queue_update`, `compaction_start`, `compaction_end`, `auto_retry_start`, `auto_retry_end`, `save_point`, `abort`, `settled`, `resources_update`

`before_prompt_render` handlers can modify structured prompt sections before PiSharp renders the final system prompt. Use `ExtensionEvent.ModifyPromptDocument()` with a `PromptDocumentPatch` for section-level edits.

```csharp
api.On(ExtensionEventNames.BeforePromptRender, (evt, cancellationToken) =>
{
    evt.ModifyPromptDocument(new PromptDocumentPatch(
        ReplaceSections:
        [
            new PromptDocumentSectionPatch(
                "skills.available",
                "<available_skills>...</available_skills>",
                Slot: "skills",
                Kind: "skills",
                ContentType: PromptDocumentContentTypes.Raw)
        ]));
    return Task.CompletedTask;
});
```

`before_prompt_render` runs before `before_agent_start`. The prompt debug document reflects `before_prompt_render` edits; later `before_agent_start` string rewrites remain compatibility-only and are not reflected in `LastPromptDocument`.

`before_agent_start` handlers can modify the system prompt or message list through `ExtensionEvent.ModifyBeforeAgentStart()`.

Native extensions can also use the cross-extension event bus directly:

```csharp
var subscription = api.Events.On("my:notification", (evt, _) =>
{
    // evt.Payload contains the sender-supplied object.
    return Task.CompletedTask;
});

await api.Events.EmitAsync("my:notification", new { Text = "hello" }, cancellationToken);

// subscription.Dispose() removes the handler.
```

Event bus delivery is ordered by registration, isolates handler failures, and records diagnostics instead of stopping later subscribers. Disposing a subscription or the extension event bus removes registered handlers.

### Resource discovery

Native extensions can contribute skill, prompt-template, and theme paths during startup by handling `resources_discover`:

```csharp
api.On(ExtensionEventNames.ResourcesDiscover, (evt, _) =>
{
    var payload = (ExtensionResourcesDiscoverPayload)evt.Payload!;
    evt.AddResourcesDiscoverPaths(
        skillPaths: [Path.Combine(payload.Cwd, ".pi", "generated-skills")],
        promptPaths: [Path.Combine(payload.Cwd, ".pi", "prompts")],
        themePaths: [Path.Combine(payload.Cwd, ".pi", "themes", "custom.json")]);
    return Task.CompletedTask;
});
```

The payload includes `Cwd` and `Reason` (`startup` today). PiSharp merges returned paths deterministically before final skill, prompt-template, and theme composition. Resource discovery does not discover or reload additional extensions from contributed paths.

### Input and session lifecycle hooks

Native extensions can use the same event names and payload/result contracts as the TypeScript bridge.

```csharp
api.On(ExtensionEventNames.Input, (evt, _) =>
{
    var input = (ExtensionInputEvent)evt.Payload!;
    if (input.Text == "/hello") evt.TransformInput("say hello", input.Images);
    if (input.Text == "/handled") evt.HandleInput();
    return Task.CompletedTask;
});
```

`input` handlers run before prompt routing in TUI, print, RPC, and server modes. `TransformInput()` replaces the text/images used by later processing; `HandleInput()` stops normal prompt handling.

`user_bash` handlers run for interactive `!`/`!!` shell requests and RPC `bash` commands:

```csharp
api.On(ExtensionEventNames.UserBash, (evt, _) =>
{
    var payload = (ExtensionUserBashPayload)evt.Payload!;
    if (payload.Command == "status")
    {
        evt.SetUserBashResult(result: new ExtensionBashResult(
            payload.Command,
            ExitCode: 0,
            Output: "ok",
            Error: ""));
    }

    return Task.CompletedTask;
});
```

The payload includes `Command`, `ExcludeFromContext`, and `Cwd`. `SetUserBashResult()` can provide `ExtensionBashOperations` to transform command metadata, or `ExtensionBashResult` to supply a handled result. The first handler that supplies either operations or a result wins; later handlers are skipped. Hook failures are isolated and shell execution still remains subject to PiSharp policy/tool boundaries.

Session replacement hooks can cancel without throwing:

```csharp
api.On(ExtensionEventNames.SessionBeforeSwitch, (evt, _) =>
{
    var payload = (ExtensionSessionBeforeSwitchEvent)evt.Payload!;
    if (payload.TargetSessionFile?.Contains("blocked", StringComparison.OrdinalIgnoreCase) == true)
        evt.CancelSessionChange("blocked by extension");
    return Task.CompletedTask;
});

api.On(ExtensionEventNames.SessionBeforeFork, (evt, _) =>
{
    var payload = (ExtensionSessionBeforeForkEvent)evt.Payload!;
    if (payload.EntryId == "blocked-entry") evt.CancelSessionChange("blocked fork");
    return Task.CompletedTask;
});
```

`SessionRuntime.NewSessionAsync()`, `SwitchSessionAsync()`, and `ForkAsync()` return a runtime result with `Cancelled`, `Reason`, and the applied session metadata when replacement succeeds.

`session_shutdown` is notification-only and runs before a successful replacement is applied and during runtime disposal:

```csharp
api.On(ExtensionEventNames.SessionShutdown, (evt, _) =>
{
    var payload = (ExtensionSessionShutdownEvent)evt.Payload!;
    // payload.Reason, payload.TargetSessionFile, payload.Session
    return Task.CompletedTask;
});
```

## Middleware

Middleware wraps extension event dispatch and can specifically intercept tool calls/results through `ExtensionMiddlewareContext`.

```csharp
api.Use(async (context, next, cancellationToken) =>
{
    if (context.BeforeToolCall?.ToolName == "bash")
    {
        context.Blocked = true;
        context.BlockReason = "bash is disabled by this extension";
        return;
    }

    await next(context, cancellationToken);
});
```

Middleware can also call `ModifyToolResult()` to replace content, details, or error state after a tool returns.

## UI API

`IExtensionUi` supports:

- `NotifyAsync()`
- `ConfirmAsync()`
- `InputAsync()`
- `SelectAsync()`
- `SetStatusAsync()` and `SetWidgetAsync()`
- title/header/footer/working indicator customization
- editor text get/set/paste/open operations
- terminal input hooks
- autocomplete providers
- custom requests with `RequestAsync()`

Interactive UI is not always available. In print/RPC/non-UI modes, `NoExtensionUi` throws `NotSupportedException` for interactive methods. Check `api.HasUi` before requiring UI.

## Providers

Native extensions can register model providers directly by implementing `IModelProvider`. This avoids TypeScript bridge serialization and lets providers use .NET HTTP, auth, and streaming primitives.

## Unload and reload notes

Plugins are loaded into collectible assembly-load contexts. `NativePluginHost.Unload(sourceId)` removes the plugin and requests unload; actual collection depends on no remaining references to plugin types/objects. Avoid static references, background tasks, and undisposed event subscriptions that keep the context alive.

## Agent coordination extension

The `PiSharp.Coordination` project is a native extension that lets multiple PiSharp agents in the same repo discover each other, exchange messages, and receive soft stale-file conflict warnings. It is a concrete example of a native extension using tools, prompt hooks, events, and middleware.

See [Agent Coordination](pisharp-agent-coordination.md) for build, install, daemon, tool, and behavior details.

## Common pitfalls

- Missing `ExtensionMetadataAttribute` prevents loading.
- The assembly must contain a concrete `IExtension` type.
- Constructors should be parameterless because the host uses `Activator.CreateInstance()`.
- UI calls must be guarded with `api.HasUi`.
- Duplicate tools/flags/sections require the correct override policy.
- Extension flags must be registered before CLI flag application to be accepted.
