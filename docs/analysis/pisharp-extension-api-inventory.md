# PiSharp Extension API Inventory

Generated for JavaScript-pi parity review. A subagent inspected PiSharp, then this inventory was verified against current source in `src/`.

## Sources inspected

- `docs/pisharp-typescript-extensions.md`
- `docs/pisharp-native-extensions.md`
- `docs/pisharp-developer-guide.md`
- `src/PiSharp.TsBridge/Node/runner/piApi.mjs`
- `src/PiSharp.TsBridge/Node/runner/uiApi.mjs`
- `src/PiSharp.TsBridge/Node/TsBridgeRunner.mjs`
- `src/PiSharp.TsBridge/TsExtensionHost.cs`
- `src/PiSharp.Extensions/IExtensionApi.cs`
- `src/PiSharp.Extensions/ExtensionManager.cs`
- `src/PiSharp.Extensions/ExtensionRuntimeBinding.cs`
- `src/PiSharp.Extensions/ExtensionParityContracts.cs`
- `src/PiSharp.Extensions/ExtensionUi.cs`
- `src/PiSharp.Extensions/ExtensionToolRegistration.cs`
- `src/PiSharp.Extensions/ExtensionSkillRegistration.cs`
- `src/PiSharp.Runtime/Runtime/RuntimeExtensionBinder.cs`
- `src/PiSharp.Cli/Modes/InteractiveMode.cs`
- `src/PiSharp.Cli/Modes/RpcMode.cs`
- `src/PiSharp.Cli/Commands/SlashCommandRegistry.cs`

## TypeScript bridge: `pi` root object

Defined in `src/PiSharp.TsBridge/Node/runner/piApi.mjs` by `createPiApi(...)`.

### Present root members

- `pi.cwd: string`
- `pi.events.on(eventName, handler)`
  - Registers a bridge-side event handler and returns an unsubscribe/disposable function.
- `pi.events.emit(eventName, payload)`
  - Dispatches to local extension handlers (when `deps.emitEvent` exists) and then runtime action `emit_event`.
- `pi.on(eventName, handler)`
  - Alias-like direct event registration.
- `pi.registerCommand(name, optionsOrDescription, maybeHandler)`
  - Stores handler in Node `state.commands`, emits `register_command` descriptor to C#.
  - Accepts modern object shape or legacy `(name, description, handler)` shape.
- `pi.registerShortcut(keys, optionsOrDescription, maybeHandler)`
  - Stores handler under `shortcut:${keys}`, emits `register_shortcut` descriptor.
- `pi.registerFlag(name, options = {})`
  - Emits `register_flag` descriptor with `type` and `defaultValue`.
- `pi.prompt.registerSection(section)`
  - Emits `register_prompt_section` descriptor.
- `pi.prompt.registerTransform(transform)`
  - Emits `register_prompt_transform` descriptor.
- `pi.registerMessageRenderer(nameOrOptions, maybeHandler)`
  - Stores Node renderer and emits `register_message_renderer`/`unregister_message_renderer` descriptors.
- `pi.registerMessageDecorator(nameOrOptions, maybeOptions, maybeHandler)`
  - Stores Node decorator and emits `register_message_decorator`/`unregister_message_decorator` descriptors.
- `pi.appendEntry(type, data)`
  - Runtime action: `append_entry`.
- `pi.getThinkingLevel()`
  - Runtime action: `get_thinking_level`.
- `pi.getActiveTools()`
  - Runtime action: `get_active_tools`.
- `pi.getAllTools()`
  - Runtime action: `get_all_tools`.
- `pi.getAllSkills()`
  - Alias to `skills.list()`; runtime action `get_all_skills`.
- `pi.getSelectedSkills()`
  - Alias to `skills.selected()`; runtime action `get_selected_skills`.
- `pi.setSelectedSkills(skillNames)`
  - Alias to `skills.select()`; runtime action `set_selected_skills`.
- `pi.skills`
  - See namespace below.
- `pi.extensions`
  - See namespace below.
- `pi.registerSkill(skill)`
  - Alias to `skills.register(skill)`.
- `pi.registerTool(tool)`
  - Stores Node tool handler and emits `register_tool` descriptor. Requires `name` and `execute` function.
- `pi.registerProvider(nameOrConfig, maybeConfig)`
  - Stores provider config and emits `register_provider` descriptor.
- `pi.unregisterProvider(name)`
  - Deletes Node provider and sends direct bridge request `unregister_provider`.
- `pi.getFlag(name)`
  - Runtime action: `get_flag`.
- `pi.getFlags()`
  - Runtime action: `get_flags`.
- `pi.sendMessage(message, options = {})`
  - Runtime action: `send_message`.
- `pi.sendUserMessage(content, options = {})`
  - Runtime action: `send_user_message`.
- `pi.setActiveTools(toolNames)`
  - Runtime action: `set_active_tools`.
- `pi.setModel(model)`
  - Runtime action: `set_model`.
- `pi.setThinkingLevel(level)`
  - Runtime action: `set_thinking_level`.
- `pi.reload()`
  - Runtime action: `reload_extensions`.
- `pi.session`
  - See namespace below.
- `pi.resources`
  - See namespace below.
- `pi.ui`
  - Created by `createUiApi(...)`; see UI section.

### Missing root members relative to JavaScript pi

- `pi.getCommands()` is **absent** from `piApi.mjs`.
- `pi.exec(...)` is **absent**.
- `pi.setSessionName(...)`, `pi.getSessionName()`, and `pi.setLabel(...)` are not root aliases; partial equivalents exist under `pi.session`.
- `pi.hasUI` is not exposed on root `pi` in `piApi.mjs`.

## TypeScript bridge: `pi.skills`

Defined as `skillApi` in `piApi.mjs`.

- `pi.skills.register(skill)`
  - Requires `name` and `description`.
  - Emits `register_skill` descriptor with `content`, `filePath`, `disableModelInvocation`, `override`.
- `pi.skills.list()`
  - Runtime action: `get_all_skills`.
- `pi.skills.selected()`
  - Runtime action: `get_selected_skills`.
- `pi.skills.select(skillNames)`
  - Runtime action: `set_selected_skills`.

## TypeScript bridge: `pi.extensions`

Defined as `extensionsApi` in `piApi.mjs`.

- `pi.extensions.provide(key, api)`
  - Provides an in-process extension service to other TS extensions.
- `pi.extensions.get(key)`
  - Gets a provided service if available.
- `pi.extensions.waitFor(key, options)`
  - Waits for a service.
- `pi.extensions.declare(options = {})`
  - Records service provides/consumes metadata and can mark descriptor activation eager.

No `pi.extensions.getCommands()` exists in current `piApi.mjs`.

## TypeScript bridge: `pi.prompt`

- `pi.prompt.registerSection(section)`
- `pi.prompt.registerTransform(transform)`

## TypeScript bridge: `pi.session`

- `pi.session.appendEntry(type, data)`
  - Runtime action: `append_entry`.
- `pi.session.setEntryLabel(entryId, label)`
  - Runtime action: `set_entry_label`.
- `pi.session.getName()`
  - Runtime action: `get_session_name`.
- `pi.session.setName(name)`
  - Runtime action: `set_session_name`.

No `pi.session.newSession`, `pi.session.switchSession`, `pi.session.sendUserMessage`, or `pi.session.waitForIdle` exists in current `piApi.mjs`.

## TypeScript bridge: `pi.resources`

- `pi.resources.list()`
  - Runtime action: `list_resources`.
- `pi.resources.read(uri)`
  - Runtime action: `read_resource`.

## TypeScript bridge: `pi.ui` / `ctx.ui`

Defined in `src/PiSharp.TsBridge/Node/runner/uiApi.mjs`. Requests are serialized as `ui_request` messages to C#.

Present methods/properties:

- `ui.theme`
  - Bridge theme with `fg`, `bg`, `style`, `bold`, `strikethrough`.
- `ui.notify(message, options = {})`
- `ui.toast(message, options = {})`
- `ui.confirm(message, options = {})`
- `ui.prompt(message, options = {})`
- `ui.input(message, options = {})`
- `ui.select(message, options = {})`
- `ui.markdown(markdown, options = {})`
- `ui.details(title, body, options = {})`
- `ui.progress(id, value, options = {})`
- `ui.setStatus(key, text)`
- `ui.status(status, options = {})`
- `ui.setFooter(factory)`
- `ui.setHeader(factory)`
- `ui.panel(component, options = {})`
- `ui.setWidget(key, factory, options = {})`

Missing compared with JavaScript pi UI docs:

- `setWorkingMessage`, `setWorkingVisible`, `setWorkingIndicator`
- `setTitle`
- `setEditorText`, `getEditorText`, `pasteToEditor`, `editor`
- `addAutocompleteProvider`
- `custom`
- `setEditorComponent`, `getEditorComponent`
- `getAllThemes`, `getTheme`, `setTheme`
- `getToolsExpanded`, `setToolsExpanded`
- Theme helpers `italic` and possibly additional JS theme tokens are absent in bridge theme.

## TypeScript command/event context

Built by `createExtensionContext(extensionId)` in `src/PiSharp.TsBridge/Node/TsBridgeRunner.mjs`. `createCommandContext` simply returns `createExtensionContext`, so command handlers and event handlers currently receive the same shape.

Present context members:

- `ctx.extensionId`
- `ctx.cwd: string`
- `ctx.hasUI: boolean`
- `ctx.ui`
- `ctx.sessionManager`
  - `getSessionId(): string | undefined`
  - `getBranch(): []`
  - `getEntries(): []`
- `ctx.isIdle(): true`
- `ctx.signal: undefined`
- `ctx.abort(): void` no-op
- `ctx.hasPendingMessages(): false`
- `ctx.shutdown(): void` no-op
- `ctx.getContextUsage(): undefined`
- `ctx.compact(): void` no-op
- `ctx.getSystemPrompt(): ""`

Missing command-context members relative to JavaScript pi:

- `ctx.waitForIdle()`
- `ctx.newSession(options?)`
- `ctx.fork(entryId, options?)`
- `ctx.navigateTree(targetId, options?)`
- `ctx.switchSession(sessionPath, options?)`
- `ctx.reload()`
- Replacement-session `ctx.sendMessage(...)`
- Replacement-session `ctx.sendUserMessage(...)`
- `ctx.modelRegistry`
- `ctx.model`

Also important: `ctx.sessionManager.getBranch()` and `getEntries()` are stubs returning empty arrays, not live session state.

## Runtime actions handled by `TsExtensionHost`

Handled in `src/PiSharp.TsBridge/TsExtensionHost.cs` `RuntimeActionAsync`.

### Resource actions (allowed even without runtime binding)

- `list_resources`
- `read_resource`

### Runtime-bound actions

- `get_all_skills`
- `get_selected_skills`
- `set_selected_skills`
- `get_flag`
- `get_flags`
- `get_active_tools`
- `get_all_tools`
- `get_thinking_level`
- `send_message`
- `send_user_message`
- `append_entry`
- `set_entry_label`
- `get_session_name`
- `set_session_name`
- `set_active_tools`
- `set_model`
- `set_thinking_level`
- `reload_extensions`
- `emit_event`

### Runtime actions absent today

- `get_commands`
- `exec`
- `wait_for_idle`
- `new_session`
- `fork`
- `navigate_tree`
- `switch_session`
- `get_model` / `get_model_registry`
- richer session-manager reads beyond current stubs.

## Native C# extension API

Defined by `src/PiSharp.Extensions/IExtensionApi.cs` and implemented in `ExtensionManager.ExtensionApi`.

### `IExtensionApi`

Properties:

- `Descriptor: ExtensionDescriptor`
- `Cwd: string`
- `HasUi: bool`
- `Ui: IExtensionUi`
- `Session: IExtensionSessionApi`
- `Tools: IExtensionToolApi`
- `Skills: IExtensionSkillApi`
- `Model: IExtensionModelApi`
- `Events: IExtensionEventBus`
- `Prompt: IExtensionPromptApi`

Methods:

- `On(eventName, handler): IDisposable`
- `Use(middleware): IDisposable`
- `RegisterTool(registration): IDisposable`
- `RegisterSkill(registration): IDisposable`
- `RegisterCommand(registration): IDisposable`
- `RegisterShortcut(registration): IDisposable`
- `RegisterFlag(registration): IDisposable`
- `RegisterMessageRenderer(registration): IDisposable`
- `RegisterMessageDecorator(registration): IDisposable`
- `RegisterProvider(provider): RegisteredApiProvider`
- `RemoveProvider(api): bool`
- `GetFlag(name): object?`
- `GetFlags(): IReadOnlyDictionary<string, object?>`
- `SendMessageAsync(message, cancellationToken)`
- `SendMessageAsync(message, delivery, triggerTurn, cancellationToken)`

Native API has no direct `GetCommandsAsync` method on `IExtensionApi`, even though `ExtensionRuntimeBinding` has a `GetCommandsAsync` delegate.

### `IExtensionSessionApi`

From `ExtensionParityContracts.cs`:

- `SendMessageAsync(message, cancellationToken)`
- `SendMessageAsync(message, delivery, triggerTurn, cancellationToken)`
- `SendUserMessageAsync(content, delivery = FollowUp, cancellationToken)`
- `AppendEntryAsync(customType, data, cancellationToken)`
- `GetNameAsync(cancellationToken)`
- `SetNameAsync(name, cancellationToken)`
- `SetLabelAsync(entryId, label, cancellationToken)`

No `NewSessionAsync`, `ForkAsync`, `SwitchSessionAsync`, `NavigateTreeAsync`, or `WaitForIdleAsync` exists in the native session API.

### `IExtensionToolApi`

- `RegisterTool(registration): IDisposable`
- `GetActiveToolsAsync(cancellationToken)`
- `GetAllToolsAsync(cancellationToken)`
- `SetActiveToolsAsync(toolNames, cancellationToken)`

### `IExtensionSkillApi`

- `RegisterSkill(registration): IDisposable`
- `GetAllSkillsAsync(cancellationToken)`
- `GetSelectedSkillsAsync(cancellationToken)`
- `SetSelectedSkillsAsync(skillNames, cancellationToken)`

### `IExtensionModelApi`

- `SetModelAsync(model, cancellationToken)`
- `GetThinkingLevelAsync(cancellationToken)`
- `SetThinkingLevelAsync(level, cancellationToken)`

### `IExtensionEventBus`

- `On(eventName, handler): IDisposable`
- `EmitAsync(eventName, payload, cancellationToken)`

### `IExtensionPromptApi`

- `RegisterContributor(contributor): IDisposable`
- `RegisterSection(section): IDisposable`
- `RegisterSection(ExtensionPromptSectionRegistration): IDisposable`
- `RegisterTransform(transform): IDisposable`

### `IExtensionUi`

- `RequestAsync(request, cancellationToken)`
- `NotifyAsync(message, severity, cancellationToken)`
- `ConfirmAsync(message, cancellationToken)`
- `InputAsync(prompt, initialValue, cancellationToken)`
- `SelectAsync(prompt, options, cancellationToken)`
- `OnTerminalInput(handler)`
- `SetStatusAsync(extensionId, status, cancellationToken)`
- `SetWidgetAsync(extensionId, widget, cancellationToken)`
- `SetTitleAsync(extensionId, title, cancellationToken)`
- `GetEditorTextAsync(extensionId, cancellationToken)`
- `SetEditorTextAsync(extensionId, text, cancellationToken)`
- `SetWorkingMessageAsync(message, cancellationToken)`
- `SetWorkingVisibleAsync(visible, cancellationToken)`
- `SetWorkingIndicatorAsync(indicator, cancellationToken)`
- `SetHiddenThinkingLabelAsync(label, cancellationToken)`
- `SetFooterAsync(extensionId, footer, cancellationToken)`
- `SetHeaderAsync(extensionId, header, cancellationToken)`
- `ShowCustomAsync(extensionId, component, cancellationToken)`
- `PasteToEditorAsync(extensionId, text, cancellationToken)`
- `OpenEditorAsync(title, prefill, cancellationToken)`
- `AddAutocompleteProvider(extensionId, provider)`

Some methods have default no-op/fallback implementations and may not be backed in every UI mode.

## CLI/RPC command discovery in PiSharp

- Interactive mode `BuildCommandRegistry` registers:
  - built-ins
  - extension commands
  - skill commands (`sourceId = "skill"`, names `skill:<name>`)
  - prompt-template commands (`sourceId = "prompt-template"`, names `prompt:<name>`)
- RPC mode `BuildCommandRegistry` registers:
  - built-ins
  - extension commands
  - prompt-template commands
  - **not skill commands** currently.
- RPC `get_commands` response currently returns `{ Name, Description, SourceId }`, not JavaScript pi's `{ name, description, source, sourceInfo }` shape.

## `@juicesharp/rpiv-workflow` relevance checklist

| Surface expected by rpiv-workflow | PiSharp TypeScript bridge status |
|---|---|
| `pi.registerCommand("wf", ...)` | Present |
| `pi.getCommands()` | Missing |
| `pi.sendUserMessage(...)` fallback for continue-policy stages | Present |
| command `ctx.cwd` | Present |
| command `ctx.hasUI` | Present |
| command `ctx.ui.notify(...)` | Present |
| command `ctx.ui.setStatus(...)` | Present |
| command `ctx.sessionManager.getBranch()` | Present but stubbed empty |
| command `ctx.isIdle()` | Present but hard-coded true |
| command `ctx.waitForIdle()` | Missing |
| command `ctx.newSession({ withSession })` | Missing |
| replacement `ctx.sendUserMessage(...)` | Missing because no replacement context exists |

Conclusion: the current `/wf` failure is caused by missing `pi.getCommands()`. After that is fixed, workflow execution will still need command-context/session replacement parity.
