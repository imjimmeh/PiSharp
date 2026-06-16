# JavaScript pi Extension API Inventory

Generated for PiSharp parity review. A subagent inspected the installed JavaScript pi package, then this inventory was verified against the installed docs and declaration output.

## Sources inspected

- `C:\Users\jimme\AppData\Roaming\npm\node_modules\@earendil-works\pi-coding-agent\docs\extensions.md`
- `C:\Users\jimme\AppData\Roaming\npm\node_modules\@earendil-works\pi-coding-agent\docs\session-format.md`
- `C:\Users\jimme\AppData\Roaming\npm\node_modules\@earendil-works\pi-coding-agent\dist\index.d.ts` and related `dist/core/*.d.ts` declarations found by searching for extension context/API types.

## Extension factory

Extensions export a default sync or async factory:

```ts
export default function (pi: ExtensionAPI): void | Promise<void> {
  // register commands, tools, hooks, providers, renderers, etc.
}
```

The factory is awaited before startup continues, so startup-time registrations are visible before normal sessions begin.

## `ExtensionAPI` / `pi`

### Events

`pi.on(event, handler)` subscribes to lifecycle/runtime events. Handlers receive `(event, ctx)` where `ctx` is `ExtensionContext`. Return values are event-specific.

Supported event names documented/types exposed:

- Resource: `resources_discover`
- Session: `session_start`, `session_before_switch`, `session_before_fork`, `session_before_compact`, `session_compact`, `session_shutdown`, `session_before_tree`, `session_tree`
- Agent/turn/message: `before_agent_start`, `agent_start`, `agent_end`, `turn_start`, `turn_end`, `message_start`, `message_update`, `message_end`, `context`
- Provider/model: `before_provider_request`, `after_provider_response`, `model_select`, `thinking_level_select`
- Tool: `tool_execution_start`, `tool_execution_update`, `tool_execution_end`, `tool_call`, `tool_result`
- User input/shell: `input`, `user_bash`

Event-result capabilities include:

- `resources_discover` may return `{ skillPaths?, promptPaths?, themePaths? }`.
- `session_before_switch`, `session_before_fork`, `session_before_compact`, `session_before_tree` may cancel or customize the operation.
- `context` may return replacement `messages`.
- `before_agent_start` may inject a custom message and/or replace the chained system prompt.
- `tool_call` may block and may mutate `event.input` in place.
- `tool_result` may patch `content`, `details`, and/or `isError`.
- `message_end` may replace the finalized message while preserving role.
- `user_bash` may provide replacement operations or a replacement result.
- `input` may continue, transform, or handle raw input before skill/template expansion.

### Tool registration and control

- `pi.registerTool(definition): void`
  - Registers a model-callable tool.
  - Definition includes `name`, `label`, `description`, `parameters`, optional `promptSnippet`, `promptGuidelines`, `executionMode`, `prepareArguments`, `renderShell`, `renderCall`, `renderResult`, and `execute(toolCallId, params, signal, onUpdate, ctx)`.
  - Can be called during extension load or later.
- `pi.getActiveTools(): string[]`
  - Returns currently active tool names.
- `pi.getAllTools(): ToolInfo[]`
  - Returns all tools with `name`, `description`, `parameters`, `promptGuidelines`, and `sourceInfo`.
- `pi.setActiveTools(toolNames: string[]): void`
  - Replaces active tool set.

### Command, shortcut, and flag registration

- `pi.registerCommand(name, options): void`
  - Options include `description?`, `getArgumentCompletions?(prefix)`, and `handler(args, ctx)`.
  - Command handler receives `ExtensionCommandContext`.
  - Duplicate extension commands receive numeric invocation suffixes like `review:1`, `review:2`.
- `pi.getCommands(): SlashCommandInfo[]`
  - Returns slash commands invokable through prompt/RPC, ordered like RPC `get_commands`: extension commands, prompt-template commands, then skill commands.
  - Excludes built-in interactive-only commands such as `/model` and `/settings`.
  - Entry shape:
    ```ts
    {
      name: string;
      description?: string;
      source: "extension" | "prompt" | "skill";
      sourceInfo: {
        path: string;
        source: string;
        scope: "user" | "project" | "temporary";
        origin: "package" | "top-level";
        baseDir?: string;
      };
    }
    ```
- `pi.registerShortcut(shortcut, options): void`
  - Options include `description?` and `handler(ctx)`.
- `pi.registerFlag(name, options): void`
  - Options include `description?`, `type: "boolean" | "string"`, and `default?`.
- `pi.getFlag(name): boolean | string | undefined`
  - Reads a registered flag value.

### Messages, state, and session metadata

- `pi.sendMessage(message, options?): void`
  - Sends a custom message into the session.
  - Message includes `customType`, `content`, `display`, and optional `details`.
  - Options: `{ triggerTurn?: boolean; deliverAs?: "steer" | "followUp" | "nextTurn" }`.
- `pi.sendUserMessage(content, options?): void`
  - Sends an actual user message and always triggers a turn.
  - `content` is string or text/image blocks.
  - While streaming, `options.deliverAs` must be `"steer"` or `"followUp"`.
- `pi.appendEntry(customType, data?): void`
  - Persists extension state as a custom entry not sent to the LLM.
- `pi.setSessionName(name): void`
- `pi.getSessionName(): string | undefined`
- `pi.setLabel(entryId, label): void`
  - Sets or clears an entry label/bookmark.

### Model, thinking, provider, shell

- `pi.exec(command, args, options?): Promise<ExecResult>`
  - Executes a shell command with options such as `signal` and `timeout`.
- `pi.setModel(model): Promise<boolean>`
  - Sets the active model; returns false if credentials are unavailable.
- `pi.getThinkingLevel(): ThinkingLevel`
- `pi.setThinkingLevel(level): void`
- `pi.registerProvider(name, config): void`
  - Registers/overrides model providers and model metadata, including OAuth support and custom streaming.
- `pi.unregisterProvider(name): void`
  - Removes a provider registered by the extension and restores overridden built-ins.

### Rendering and inter-extension events

- `pi.registerMessageRenderer(customType, renderer): void`
  - Renders custom messages in TUI.
- `pi.events.on(name, handler)` / `pi.events.emit(name, payload)`
  - Shared inter-extension event bus.

## `ExtensionContext` / event/tool `ctx`

Available to event handlers, tool handlers, render hooks where applicable.

- `ctx.ui: ExtensionUIContext`
- `ctx.hasUI: boolean`
  - False in print/JSON modes; true in interactive/RPC modes.
- `ctx.cwd: string`
- `ctx.sessionManager: ReadonlySessionManager`
  - Read-only session state. Common methods: `getEntries()`, `getBranch()`, `getLeafId()`.
- `ctx.modelRegistry: ModelRegistry`
- `ctx.model: Model | undefined`
- `ctx.isIdle(): boolean`
- `ctx.signal: AbortSignal | undefined`
- `ctx.abort(): void`
- `ctx.hasPendingMessages(): boolean`
- `ctx.shutdown(): void`
- `ctx.getContextUsage(): { tokens, contextWindow, percent } | undefined`
- `ctx.compact(options?): void`
  - Options: `customInstructions?`, `onComplete?`, `onError?`.
- `ctx.getSystemPrompt(): string`

## `ExtensionCommandContext` / command `ctx`

Extends `ExtensionContext`. Only available to command handlers because these operations can deadlock from general event handlers.

- `ctx.waitForIdle(): Promise<void>`
- `ctx.newSession(options?): Promise<{ cancelled: boolean }>`
  - Options: `parentSession?`, `setup?(sessionManager)`, `withSession?(replacementCtx)`.
- `ctx.fork(entryId, options?): Promise<{ cancelled: boolean }>`
  - Options: `position?: "before" | "at"`, `withSession?(replacementCtx)`.
- `ctx.navigateTree(targetId, options?): Promise<{ cancelled: boolean }>`
  - Options: `summarize?`, `customInstructions?`, `replaceInstructions?`, `label?`.
- `ctx.switchSession(sessionPath, options?): Promise<{ cancelled: boolean }>`
  - Options: `withSession?(replacementCtx)`.
- `ctx.reload(): Promise<void>`

## `ReplacedSessionContext`

Passed to `withSession` callbacks from `newSession`, `fork`, and `switchSession`. Extends `ExtensionCommandContext` and adds replacement-session-bound message senders:

- `ctx.sendMessage(message, options?): Promise<void>`
  - Same custom message shape as `pi.sendMessage`.
  - Options: `{ triggerTurn?: boolean; deliverAs?: "steer" | "followUp" | "nextTurn" }`.
- `ctx.sendUserMessage(content, options?): Promise<void>`
  - `content` is string or text/image blocks.
  - Options: `{ deliverAs?: "steer" | "followUp" }`.

Important lifecycle rule: after a session replacement, the old command `ctx` and old `pi` session-bound actions are stale for session work. Use only the replacement context passed to `withSession`.

## `ctx.ui` / `ExtensionUIContext`

Dialog/user interaction:

- `ctx.ui.select(message, options)`
- `ctx.ui.confirm(message, bodyOrOptions?, options?)`
- `ctx.ui.input(message, placeholderOrOptions?, options?)`
- `ctx.ui.editor(title, prefill?)`
- `ctx.ui.notify(message, level?)`
- `ctx.ui.custom(factory, options?): Promise<T>`

Status/widgets/editor/theme:

- `ctx.ui.setStatus(key, text | undefined)`
- `ctx.ui.setWorkingMessage(message?)`
- `ctx.ui.setWorkingVisible(visible)`
- `ctx.ui.setWorkingIndicator(indicator?)`
- `ctx.ui.setWidget(key, contentOrFactory | undefined, options?)`
- `ctx.ui.setFooter(factory | undefined)`
- `ctx.ui.setHeader(factory | undefined)`
- `ctx.ui.setTitle(title)`
- `ctx.ui.setEditorText(text)`
- `ctx.ui.getEditorText(): string`
- `ctx.ui.pasteToEditor(text)`
- `ctx.ui.addAutocompleteProvider(factory)`
- `ctx.ui.setToolsExpanded(expanded)`
- `ctx.ui.getToolsExpanded(): boolean`
- `ctx.ui.setEditorComponent(factory | undefined)`
- `ctx.ui.getEditorComponent()`
- `ctx.ui.getAllThemes()`
- `ctx.ui.getTheme(name)`
- `ctx.ui.setTheme(themeOrName)`
- `ctx.ui.theme` with color/style helpers such as `fg`, `bg`, `bold`, `italic`, `strikethrough`.

## `SessionManager` / `ReadonlySessionManager`

Static methods:

- `SessionManager.create(cwd, sessionDir?)`
- `SessionManager.open(path, sessionDir?)`
- `SessionManager.continueRecent(cwd, sessionDir?)`
- `SessionManager.inMemory(cwd?)`
- `SessionManager.forkFrom(sourcePath, targetCwd, sessionDir?)`
- `SessionManager.list(cwd, sessionDir?, onProgress?)`
- `SessionManager.listAll(onProgress?)`

Instance methods documented:

- Session management: `newSession(options?)`, `setSessionFile(path)`, `createBranchedSession(leafId)`
- Appending: `appendMessage`, `appendThinkingLevelChange`, `appendModelChange`, `appendCompaction`, `appendCustomEntry`, `appendSessionInfo`, `appendCustomMessageEntry`, `appendLabelChange`
- Tree navigation/read: `getLeafId`, `getLeafEntry`, `getEntry`, `getBranch`, `getTree`, `getChildren`, `getLabel`, `branch`, `resetLeaf`, `branchWithSummary`
- Context/info: `buildSessionContext`, `getEntries`, `getHeader`, `getSessionName`, `getCwd`, `getSessionDir`, `getSessionId`, `getSessionFile`, `isPersisted`

## Surfaces used by `@juicesharp/rpiv-workflow`

High-confidence from package source:

- `pi.registerCommand("wf", ...)`
- `pi.getCommands()` returning at least skill commands with `source: "skill"` and names like `skill:<name>`
- Command `ctx.hasUI`, `ctx.cwd`, `ctx.ui.notify`, `ctx.ui.setStatus`
- Command `ctx.sessionManager.getBranch()`
- Command `ctx.isIdle()`
- Command `ctx.waitForIdle()`
- Command `ctx.newSession({ withSession })`
- Replacement `ctx.sendUserMessage()`
- Root `pi.sendUserMessage()` as fallback for continue-policy stages
