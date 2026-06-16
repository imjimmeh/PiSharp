# PiSharp vs JavaScript pi Extension API Parity Gaps

Compares:

- `docs/analysis/js-pi-extension-api-inventory.md`
- `docs/analysis/pisharp-extension-api-inventory.md`

## Executive summary

The immediate `@juicesharp/rpiv-workflow` error is confirmed:

```text
host.getCommands is not a function or its return value is not iterable
```

JavaScript pi exposes `pi.getCommands()` synchronously. PiSharp's TypeScript bridge does not expose `getCommands` at all, even though PiSharp has partial internal command-registry machinery and an unused `ExtensionRuntimeBinding.GetCommandsAsync` delegate.

After `getCommands` is added, `rpiv-workflow` will likely hit further missing session-control surfaces: command `ctx.newSession`, `ctx.waitForIdle`, and replacement-session `ctx.sendUserMessage`. Current PiSharp TypeScript command contexts are the same stubbed context used for event handlers.

## Critical gaps for `@juicesharp/rpiv-workflow`

| JS pi surface | Required behavior | PiSharp status | Impact |
|---|---|---|---|
| `pi.getCommands(): SlashCommandInfo[]` | Return extension, prompt, and skill commands; entries include `source: "skill"` and names like `skill:<name>`. | Missing from `piApi.mjs`; no `get_commands` runtime action in `TsExtensionHost`. | Current crash before workflow can preflight skills. |
| Command `ctx.newSession({ withSession })` | Create replacement session, call `withSession(replacementCtx)`, invalidate old session-bound ctx. | Missing from TS command context. | Fresh-policy workflow stages cannot run. |
| Command `ctx.waitForIdle()` | Wait until agent finishes streaming after programmatic send. | Missing from TS command context. | Continue-policy workflow stages cannot safely advance. |
| Replacement `ctx.sendUserMessage(...)` | Available inside `withSession` callback, bound to the replacement session. | Missing because no replacement context support exists. | Fresh-policy stage cannot submit `/skill:<stage>` prompt. |
| `ctx.sessionManager.getBranch()` | Return live current branch entries. | Present but stubbed to `[]`. | Outcome collectors/transcript readers cannot inspect actual stage output. |
| `ctx.isIdle()` | Report real runtime idle state. | Present but hard-coded `true`. | Workflow may race the agent. |
| Root `pi.sendUserMessage(...)` | Send user message to active session, queue correctly during streaming. | Present via `send_user_message` runtime action. | Continue-policy fallback may work only for simple cases; delivery semantics need parity verification. |
| `ctx.ui.notify`, `ctx.ui.setStatus` | Status/user feedback. | Present in bridge UI API. | Likely enough for workflow status. |
| `ctx.cwd`, `ctx.hasUI` | Current working directory and UI availability. | Present. | OK. |

## Command discovery gaps

### Missing bridge API

JavaScript pi:

```ts
pi.getCommands(): SlashCommandInfo[]
```

PiSharp:

- `src/PiSharp.TsBridge/Node/runner/piApi.mjs` does not define `getCommands`.
- `src/PiSharp.TsBridge/TsExtensionHost.cs` does not handle a `get_commands` runtime action.
- `src/PiSharp.Extensions/ExtensionRuntimeBinding.cs` contains `GetCommandsAsync`, but nothing wires it from runtime or exposes it to TS/native API.

### Shape mismatch

JavaScript pi command entry:

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

PiSharp RPC `get_commands` currently returns:

```cs
new { command.Name, command.Description, command.SourceId }
```

That is not enough for source-compatible JavaScript extensions unless the bridge maps `SourceId` to JS-compatible `source` and synthesizes `sourceInfo`.

### Skill command omission in RPC path

PiSharp interactive command registry includes skill commands. PiSharp RPC command registry currently does not:

- Interactive: built-ins + extension + skills + prompt templates.
- RPC: built-ins + extension + prompt templates, **no skills**.

For `rpiv-workflow`, `getCommands()` must include skill commands because it snapshots registered skills by filtering `command.source === "skill"`.

## Command context/session-control gaps

JavaScript pi command handlers receive `ExtensionCommandContext`, which extends base `ExtensionContext` with:

- `waitForIdle()`
- `newSession(options?)`
- `fork(entryId, options?)`
- `navigateTree(targetId, options?)`
- `switchSession(sessionPath, options?)`
- `reload()`

`newSession`, `fork`, and `switchSession` may provide a `withSession(replacementCtx)` callback. The replacement context adds:

- `sendMessage(...)`
- `sendUserMessage(...)`

PiSharp TypeScript command handlers currently receive `createCommandContext(extensionId)`, which simply returns `createExtensionContext(extensionId)`. That context has no command-only methods and no replacement-session support.

## Session manager/state gaps

JavaScript pi `ctx.sessionManager` is a read-only `SessionManager` view with live methods including:

- `getEntries()`
- `getBranch(fromId?)`
- `getLeafId()`
- `getLeafEntry()`
- `getEntry(id)`
- `getTree()`
- `getChildren(parentId)`
- `getLabel(id)`
- `getHeader()`
- `getSessionName()`
- `getCwd()`
- `getSessionDir()`
- `getSessionId()`
- `getSessionFile()`
- `isPersisted()`

PiSharp TypeScript bridge currently exposes only:

```js
sessionManager: {
  getSessionId: () => runtimeSessionId,
  getBranch: () => [],
  getEntries: () => [],
}
```

This is enough for trivial extensions that only check the property exists, but not for workflow outcome extraction or state reconstruction.

## Base `ExtensionContext` gaps

JavaScript pi base `ctx` includes:

- `ctx.ui`
- `ctx.hasUI`
- `ctx.cwd`
- `ctx.sessionManager`
- `ctx.modelRegistry`
- `ctx.model`
- `ctx.isIdle()`
- `ctx.signal`
- `ctx.abort()`
- `ctx.hasPendingMessages()`
- `ctx.shutdown()`
- `ctx.getContextUsage()`
- `ctx.compact(options?)`
- `ctx.getSystemPrompt()`

PiSharp TS bridge status:

| Surface | Status |
|---|---|
| `ui` | Present, partial. |
| `hasUI` | Present. |
| `cwd` | Present. |
| `sessionManager` | Present but heavily stubbed. |
| `modelRegistry` | Missing. |
| `model` | Missing. |
| `isIdle()` | Present but hard-coded true. |
| `signal` | Present but always `undefined`. |
| `abort()` | Present but no-op. |
| `hasPendingMessages()` | Present but hard-coded false. |
| `shutdown()` | Present but no-op. |
| `getContextUsage()` | Present but returns undefined. |
| `compact()` | Present but no-op. |
| `getSystemPrompt()` | Present but returns empty string. |

## Root `pi` API gaps beyond workflows

JavaScript pi root `ExtensionAPI` includes:

- `on`
- `registerTool`
- `registerCommand`
- `getCommands`
- `registerShortcut`
- `registerFlag`
- `getFlag`
- `registerMessageRenderer`
- `sendMessage`
- `sendUserMessage`
- `appendEntry`
- `setSessionName`
- `getSessionName`
- `setLabel`
- `exec`
- `getActiveTools`
- `getAllTools`
- `setActiveTools`
- `setModel`
- `getThinkingLevel`
- `setThinkingLevel`
- `registerProvider`
- `unregisterProvider`
- `events`

PiSharp TS bridge has many but misses or diverges on:

| JS pi root API | PiSharp status |
|---|---|
| `getCommands` | Missing. |
| `exec` | Missing. |
| `setSessionName` | Root alias missing; `pi.session.setName` exists. |
| `getSessionName` | Root alias missing; `pi.session.getName` exists. |
| `setLabel` | Root alias missing; `pi.session.setEntryLabel` exists. |
| `getActiveTools` / `getAllTools` | Present but async in PiSharp bridge; JS pi docs/types are sync. |
| `getThinkingLevel` | Present but async in PiSharp bridge; JS pi docs/types are sync. |
| `getFlag` | Present but async in PiSharp bridge; JS pi docs/types are sync. |
| `sendMessage` / `sendUserMessage` | Present but async/Promise-returning in PiSharp bridge; JS pi type is void but runtime may be awaitable. |
| `registerMessageDecorator` | PiSharp has this extra surface; JS docs emphasize renderer only. |
| `skills`, `prompt`, `resources`, `extensions` namespaces | PiSharp-specific/compat additions not in the core JS `ExtensionAPI` docs. |

Async-vs-sync differences can still break extensions that use returned arrays/values immediately without `await`.

## UI parity gaps

PiSharp bridge UI has useful basics:

- `notify` / `toast`
- `confirm`
- `prompt` / `input`
- `select`
- `markdown`
- `details`
- `progress`
- `setStatus` / `status`
- `setFooter`
- `setHeader`
- `panel`
- `setWidget`
- basic `theme`

JavaScript pi UI includes additional surfaces not currently in the TS bridge UI:

- `setWorkingMessage`
- `setWorkingVisible`
- `setWorkingIndicator`
- `setTitle`
- `setEditorText`
- `getEditorText`
- `pasteToEditor`
- `editor`
- `addAutocompleteProvider`
- `custom`
- `setEditorComponent`
- `getEditorComponent`
- `getAllThemes`
- `getTheme`
- `setTheme`
- `getToolsExpanded`
- `setToolsExpanded`
- richer theme helpers/tokens (`italic`, etc.)

Native `IExtensionUi` exposes several of these, but the TypeScript bridge does not currently surface all of them through `uiApi.mjs`.

## PiSharp-only additions (intentional divergences)

These members exist in PiSharp but have no equivalent in JS Pi. Extensions using them are no-ops or gracefully degraded on JS Pi.

- **`ctx.ui.registerMenuItem(menu, item)`** — menu bar item registration. Adds a named item under a top-level menu title (e.g. `"Tools"`). JS Pi has no menu API; the default `IExtensionUi.RegisterMenuItemAsync` implementation is a no-op (`Task.CompletedTask`), so extensions calling this work correctly on PiSharp and are silently ignored on JS Pi.

- **`ctx.ui.setWidget` placements `"sidebar-left"` / `"sidebar-right"`** — additional placement values for the TUI's collapsible left and right sidebars. JS Pi ignores unknown placement values (graceful degradation), so extensions using these placements do not break on JS Pi.

## Event parity notes

PiSharp has event registration and a growing event bridge, but parity should be checked event-by-event against JavaScript pi's event list and return semantics:

- `resources_discover`
- `session_start`, `session_before_switch`, `session_before_fork`, `session_before_compact`, `session_compact`, `session_shutdown`, `session_before_tree`, `session_tree`
- `context`
- `before_provider_request`, `after_provider_response`
- `before_agent_start`, `agent_start`, `agent_end`
- `turn_start`, `turn_end`
- `message_start`, `message_update`, `message_end`
- `tool_execution_start`, `tool_execution_update`, `tool_execution_end`
- `model_select`, `thinking_level_select`
- `tool_call`, `tool_result`
- `user_bash`
- `input`

Known implemented recent areas exist in PiSharp (resources discovery, user bash, message renderers/decorators, event bus), but this comparison did not prove every payload/return contract is JavaScript-identical.

## Native C# API gaps compared with JS extension ergonomics

Native C# extensions expose richer typed registries, but not all JS command/session ergonomics:

- `IExtensionApi` has no `GetCommands` method despite `ExtensionRuntimeBinding.GetCommandsAsync` existing.
- `IExtensionSessionApi` has `SendUserMessageAsync`, state append, session name, and label operations, but no `NewSessionAsync`, `WaitForIdleAsync`, `ForkAsync`, `SwitchSessionAsync`, or `NavigateTreeAsync`.
- Native API can be extended independently, but TypeScript package compatibility depends on the bridge, not just native contracts.

## Recommended implementation order

### Phase 1 — Fix the immediate `/wf` crash

1. Add `pi.getCommands()` in `src/PiSharp.TsBridge/Node/runner/piApi.mjs`.
2. Add `get_commands` runtime action in `TsExtensionHost.RuntimeActionAsync`.
3. Wire `ExtensionRuntimeBinding.GetCommandsAsync` in `RuntimeExtensionBinder`.
4. Return JS-compatible command objects: `name`, `description`, `source`, `sourceInfo`.
5. Ensure skill commands are included in the command list used by the bridge.
6. Add a regression test with a TS extension that calls `pi.getCommands()` during activation and/or command handling.

### Phase 2 — Make workflow fresh stages possible

1. Add command-context actions to the TS bridge: `waitForIdle`, `newSession`, `fork`, `navigateTree`, `switchSession`, `reload`.
2. Create replacement-session contexts for `withSession` callbacks.
3. Expose replacement `sendMessage` and `sendUserMessage` on that context.
4. Preserve JavaScript pi stale-context semantics: after session replacement, old session-bound ctx/handles should not be used for session work.

### Phase 3 — Make workflow outcome extraction reliable

1. Replace TS `sessionManager.getBranch()` and `getEntries()` stubs with live session state.
2. Add at least `getLeafId`, `getSessionFile`, `getSessionName`, and `getLabel` for close parity.
3. Ensure session state is synchronized before `tool_call` and command post-processing where JavaScript pi guarantees it.

### Phase 4 — Broader extension parity

1. Add root aliases `setSessionName`, `getSessionName`, `setLabel`.
2. Add or intentionally document lack of `pi.exec`.
3. Fill TS bridge UI gaps, prioritizing `setTitle`, editor methods, `custom`, and autocomplete providers.
4. Resolve async-vs-sync divergences for getters where JavaScript extensions expect immediate arrays/values, or document compatibility limits.

## Minimal `rpiv-workflow` acceptance test sketch

A parity-focused test should load a TS extension equivalent to:

```ts
import workflow from "@juicesharp/rpiv-workflow";

export default function activate(pi) {
  workflow(pi);
  pi.registerCommand("assert-workflow-host", {
    description: "Assert workflow host surfaces",
    handler: async (_args, ctx) => {
      const commands = pi.getCommands();
      if (!Array.isArray(commands)) throw new Error("getCommands did not return an array");
      if (!commands.some((c) => c.source === "skill" && c.name.startsWith("skill:"))) {
        throw new Error("skill commands missing");
      }
      if (typeof ctx.newSession !== "function") throw new Error("ctx.newSession missing");
      if (typeof ctx.waitForIdle !== "function") throw new Error("ctx.waitForIdle missing");
      if (typeof ctx.sessionManager.getBranch !== "function") throw new Error("getBranch missing");
    },
  });
}
```

Then separately run a tiny workflow with one fresh skill stage and verify a real session branch is captured.
