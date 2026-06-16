# PiSharp TypeScript Extension Compatibility

PiSharp can load Pi-style TypeScript extensions through `PiSharp.TsBridge`. TypeScript extensions run out-of-process in Node.js and communicate with the .NET runtime over JSON-RPC.

Use native .NET extensions when you want direct .NET integration. Use TypeScript extensions when you want compatibility with existing Pi extension code.

## Extension locations

TypeScript extension discovery uses Pi-compatible locations:

- `~/.pi/agent/extensions/*.ts` and `*.js`
- `~/.pi/agent/extensions/*/index.ts` and `index.js`
- `<cwd>/.pi/extensions/*.ts` and `*.js`
- `<cwd>/.pi/extensions/*/index.ts` and `index.js`
- `settings.extensions`
- CLI `--extension <path>` / `-e`
- package resources from `pi.extensions` or package `extensions/`

Non-`.dll` extension paths are treated as TypeScript/JavaScript bridge candidates.

## Bridge startup

At runtime startup, PiSharp:

1. Starts `node <base>/Node/TsBridgeRunner.mjs`.
2. Opens a JSON-RPC connection over the process stdin/stdout.
3. Sends `initialize` with extension paths, cache options, and UI availability.
4. Loads or replays extension descriptors.
5. Registers bridged tools, commands, shortcuts, flags, prompt sections/transforms, and providers in the same `ExtensionRegistry` used by native extensions.
6. Forwards extension-visible events to the Node sidecar.
7. Calls back into Node when bridged tools, commands, renderers, providers, or UI handlers are invoked.

Node.js must be available as `node` unless `TsBridgeOptions.NodeExecutable` is supplied programmatically.

## Descriptor cache

PiSharp stores TypeScript extension descriptor cache files under:

```text
~/.pi/PiSharp/cache/ts-bridge/descriptors
```

A descriptor contains schema version, extension path, source hash, dependency hashes, and registration metadata for tools, commands, shortcuts, flags, prompt sections/transforms, and providers.

On startup, PiSharp can replay a valid descriptor immediately. The full TypeScript module is activated later when a registered command/tool/provider needs it. Cache replay is skipped if:

- The descriptor is missing.
- The schema version is unsupported.
- The extension path no longer matches.
- The source hash changed.
- Any recorded dependency is missing or changed.
- The descriptor provides or consumes live extension services.
- The descriptor declares `activation: "eager"`.

The cache is an optimization only; failed cache reads fall back to eager loading.

## Extension services

TypeScript extensions can expose live JavaScript APIs to other TypeScript extensions in the same bridge process:

```javascript
export default async function activate(pi) {
  pi.extensions.provide("pisharp.embeddings", {
    async embedMany(request) {
      return { embeddings: [], providerId: "demo", model: "demo", dimensions: 0 };
    }
  });
}
```

Consumers can wait for the service by key:

```javascript
export default async function activate(pi) {
  const embeddings = await pi.extensions.waitFor("pisharp.embeddings", { timeoutMs: 5000 });
  const result = await embeddings.embedMany({ inputs: ["hello"] });
}
```

Services are live in-process JavaScript objects. They are not serialized through .NET and are intended for extension-to-extension APIs such as embedding providers. Extensions that provide or consume services are activated eagerly instead of descriptor-cache-only replay so their live API objects exist before consumers use them.

Providers and consumers can also declare service metadata before a service is available:

```javascript
pi.extensions.declare({ provides: ["pisharp.embeddings"], activation: "eager" });
pi.extensions.declare({ consumes: ["pisharp.embeddings"] });
```

## Process-backed workflow extension

Bundled TypeScript extensions can provide orchestration services without adding runtime APIs to PiSharp core. `extensions/workflow-sessions` registers the `workflow_run` tool and the eager `pisharp.workflows` service; it runs workflow nodes as child `pisharp --print` processes so each node is a normal PiSharp session. See `extensions/workflow-sessions/README.md` for tool inputs, DAG shape, safety defaults, and metadata storage.

## In-process subagent sessions

TypeScript extensions can create isolated child agent sessions through the Pi-compatible `createAgentSession()` API from `@pi-coding-agent`:

```javascript
import { createAgentSession } from "@pi-coding-agent";

export default async function activate(pi) {
  const { session } = await createAgentSession({ model: { id: "example-model" } });

  const subscription = session.subscribe(event => {
    if (event.type === "agent_end") {
      console.log(event.messages?.at(-1)?.content);
    }
  });

  const result = await session.prompt("Investigate this problem");
  await session.followUp("Check one more edge case");
  subscription.dispose();
  await session.dispose();
}
```

`AgentSession.prompt()` is backed by PiSharp's runtime subagent service, not by `completeSimple` or a one-shot completion shim. Each child session owns its own JSONL session, harness queue, model/thinking-level state, cancellation scope, and event subscription list. Parent session events and child session events are kept separate.

`session.messages` exposes the child session's completed `AgentMessage` transcript, matching JavaScript Pi's `AgentSession.messages` contract. Extensions can use this as a fallback for final output when a provider does not stream `message_update.assistantMessageEvent.type === "text_delta"` events. Runtime session-tree entries remain available in the snapshot separately as `entries`; they are not returned from `AgentSession.messages`.

The bridge forwards JavaScript Pi-compatible child lifecycle events to subscribers registered with `session.subscribe()`. Supported child controls are:

- `session.prompt(content, options?)`
- `session.steer(text)`
- `session.followUp(text)`
- `session.abort()`
- `session.compact(customInstructions?)`
- `session.setModel(model)`
- `session.setThinkingLevel(level)`
- `session.dispose()`

Child event delivery is scoped by `sessionId`, so listeners on one `AgentSession` do not receive events from another child session. PiSharp forwards child events asynchronously and batches short bursts into `subagents:session:events` bridge notifications while preserving event order within each child session. `AgentSession.prompt()` and other child controls do not wait for `session.subscribe()` callbacks to finish; extensions that need to observe lifecycle completion should update state inside the subscription callback or wait for their own signal. Disposing an `AgentSession` removes the runtime event subscription and releases the child harness/session handle.

## Embedding provider extension

PiSharp does not provide embedding models in core. Load a separate extension to expose embeddings to other extensions:

```bash
pisharp --extension ./extensions/pisharp-embeddings
```

Configure the default OpenAI-compatible provider with environment variables:

```bash
PISHARP_EMBEDDINGS_API_KEY=...
PISHARP_EMBEDDINGS_MODEL=text-embedding-3-small
PISHARP_EMBEDDINGS_BASE_URL=https://api.openai.com/v1
```

The extension provides `pisharp.embeddings`:

```javascript
const embeddings = await pi.extensions.waitFor("pisharp.embeddings", { timeoutMs: 5000 });
const response = await embeddings.embedMany({ inputs: ["query", "document"] });
```

Other extensions can register additional providers at runtime:

```javascript
embeddings.registerProvider({
  id: "local",
  label: "Local embeddings",
  async embedMany(request) {
    return { embeddings: [], providerId: "local", model: request.model, dimensions: 0 };
  }
});
```

## Pre-render prompt document hooks

TypeScript extensions can inspect structured prompt sections immediately before PiSharp renders the system prompt by listening for `before_prompt_render`.

The event payload is DTO-based and does not expose raw .NET prompt-content records:

- `prompt` — the current user prompt after slash-skill expansion.
- `sections` — rendered prompt section DTOs with `id`, `kind`, `slot`, `priority`, `contentType`, `content`, and `sourceId`.

Handlers return a `{ patch }` object:

```javascript
export default async function activate(pi) {
  pi.on('before_prompt_render', async event => ({
    patch: {
      replaceSections: [{
        id: 'skills.available',
        slot: 'skills',
        kind: 'skills',
        contentType: 'raw',
        content: '<available_skills>...</available_skills>'
      }]
    }
  }));
}
```

Supported patch fields:

- `removeSectionIds: string[]`
- `replaceSections: PromptDocumentSectionPatch[]`
- `appendSections: PromptDocumentSectionPatch[]`

`before_prompt_render` runs before the legacy `before_agent_start` event. Use it for structured section edits. Use `before_agent_start` only when an extension intentionally needs final rendered string compatibility.

Event handlers live in the activated TypeScript module. Descriptor-cache-only replay records static registrations, not runtime event handlers; extensions that must participate on the first prompt should use eager activation or declare/provide/consume a service.

## Relevance-filtered skills extension

Load the embeddings provider extension first, then the skill selector extension:

```bash
pisharp --extension ./extensions/pisharp-embeddings \
        --extension ./extensions/relevance-filtered-skills
```

The selector consumes `pisharp.embeddings`, embeds skill `name + description`, embeds the current user prompt, and patches the current turn's structured `skills.available` prompt section during `before_prompt_render`. It does not call `pi.skills.select()` and does not change the full catalog or explicit `/skill:name` behavior.

Tuning environment variables:

```bash
PISHARP_SKILL_RELEVANCE_MAX_SKILLS=8
PISHARP_SKILL_RELEVANCE_TIMEOUT_MS=5000
PISHARP_SKILL_RELEVANCE_MIN_SCORE=-1
```

If the embeddings service is unavailable, ranking fails, or no skills pass the score threshold, the extension returns no patch and PiSharp keeps the original prompt section.

## Supported registrations

The bridge protocol supports descriptors for:

- Tools (`TsToolDefinition`)
- Commands (`TsCommandRegistration`)
- Shortcuts (`TsShortcutRegistration`)
- Flags (`TsFlagRegistration`)
- Prompt sections (`TsPromptSectionRegistration`)
- Prompt transforms (`TsPromptTransformRegistration`)
- Providers (`TsProviderRegistration`)
- Extension service metadata (`providesServices`, `consumesServices`, and `activation`)
- Message renderers and decorators

Tool definitions include name, label, description, JSON parameters, optional execution mode, prompt snippet/guidelines, shell/render metadata, and render-call/render-result capability flags.

Message renderer handlers use the JavaScript Pi-compatible `(message, options, theme) => component` callback shape. The component must expose a `render(width)` method that returns an array of strings. PiSharp applies TUI safety guards after extension rendering, including row clipping and control-sequence stripping.

```javascript
export default function activate(pi) {
  const renderer = pi.registerMessageRenderer({
    name: "compact-card",
    rowType: "Custom",
    customType: "my-card",
    handler: (message, options, theme) => ({
      render(width) {
        return [
          `[${message.customType}]`,
          String(message.content),
          options.expanded ? "expanded" : "collapsed",
        ];
      },
    }),
  });

  const decorator = pi.registerMessageDecorator("assistant-badge", { order: 10 }, (context, rows) =>
    rows.map(row => ({ ...row, text: `[ext] ${row.text}` }))
  );

  // Later, or during extension disposal:
  // renderer.dispose();
  // decorator.dispose();
}
```

Renderers can target a built-in `rowType` or a `customType` for custom session entries. If a custom renderer returns no rows, or a matching renderer is unavailable, the TUI falls back to the built-in row renderer which labels the entry as `[customType]`. In non-UI modes registrations are accepted but have no visible effect.

### Custom Messages

TypeScript extensions can send visible custom messages and register custom renderers for their `customType`.

```typescript
export async function activate(pi) {
  pi.registerMessageRenderer("approval-card", (message, options, theme) => ({
    render(width) {
      return [
        `[${message.customType}]`,
        String(message.content),
        options.expanded ? "expanded" : "collapsed",
      ];
    },
  }));

  await pi.sendMessage({
    customType: "approval-card",
    content: "Approve deployment?",
    display: true,
    details: { requestId: "deploy-123" },
  });
}
```

`pi.sendMessage` with a custom message object appends a visible custom message by default. Pass a second argument to control delivery:

- `{ deliverAs: "nextTurn" }` — deliver as a `UserMessage` participating in the next agent turn.
- `{ deliverAs: "steer" }` — deliver as a steering message.
- `{ deliverAs: "followUp" }` — deliver as a follow-up message.
- `{ triggerTurn: true }` — deliver and trigger a new agent turn immediately.

When no second argument is provided, the message is appended as a `CustomMessageEntry` and displayed in the transcript when `display` is `true`.

## Tool execution

When the model calls a bridged TypeScript tool, PiSharp sends a JSON-RPC request to Node with:

- `toolCallId`
- tool name
- JSON parameters

The Node side returns content, details, terminate, and error state. PiSharp maps this into `AgentToolResult<object?>` and publishes normal tool events.

## Providers

TypeScript extensions can register providers through bridge descriptors. PiSharp adapts them with `TsProviderAdapter` and registers them with `PiSharp.Ai.PublicApi`. If the provider has callback-based behavior, PiSharp calls back into Node as needed.

Native .NET providers are usually better for heavy streaming or auth logic because they avoid bridge serialization overhead.

## Events

PiSharp maps `AgentHarnessEvent` values to extension event names and forwards them to the sidecar. Supported names match the native extension event surface, including session, turn, message, tool, provider, model, compaction, queue, and resource-update events.

Main-session startup `session_start` delivery remains awaited so startup registrations, such as tools registered from a `session_start` handler, are available when runtime creation returns. Ordinary non-mutating harness events are queued and forwarded by a background bridge worker so slow TypeScript listeners do not block the agent harness or TUI. Mutating hooks such as `before_agent_start`, `before_prompt_render`, `input`, `user_bash`, and session switch/fork hooks still use request/response delivery because their return values affect runtime behavior.

When UI becomes available after the bridge starts, PiSharp updates the sidecar and forwards a `session_start` event with `ui_ready` semantics.

TypeScript extensions can also subscribe to and emit cross-extension events:

```javascript
export default async function activate(pi) {
  const sub = pi.events.on("my:notification", payload => {
    console.log(payload);
  });

  await pi.events.emit("my:notification", { text: "hello" });

  // sub.dispose() or sub.unsubscribe() removes the handler.
}
```

`pi.events.emit(name, payload)` delivers to native and TypeScript subscribers in registration order. Handler failures are isolated and recorded as diagnostics instead of aborting later handlers. Disposing the returned subscription removes the event handler.

## Resources

TypeScript extensions can inspect loaded resources without gaining arbitrary filesystem access:

```javascript
export default async function activate(pi) {
  const resources = await pi.resources.list();
  const firstSkill = resources.find(resource => resource.kind === "skill");
  if (firstSkill) {
    const text = await pi.resources.read(firstSkill.path);
  }
}
```

`pi.resources.list()` returns metadata for loaded concrete resource files. `pi.resources.read(path)` succeeds only for paths already in the loaded resource set.

`resources_discover` runs during runtime startup after extensions are activated and before final skill, prompt-template, and theme composition:

```javascript
export default function activate(pi) {
  pi.on("resources_discover", event => ({
    skillPaths: ["./.pi/generated-skills"],
    promptPaths: ["./.pi/prompts"],
    themePaths: ["./.pi/themes/custom.json"]
  }));
}
```

The payload contains `cwd` and `reason` (`"startup"` today). Return values support `skillPaths`, `promptPaths`, and `themePaths`. PiSharp merges those paths deterministically, reloads prompt templates/themes from the merged set before runtime finalization, and does not discover additional extensions from those contributed paths.

## JavaScript-compatible input and lifecycle hooks

TypeScript extensions can register JavaScript-compatible handlers for the safe hook baseline:

- `input` runs before normal prompt routing. Return `{ action: "continue" }`, `{ action: "transform", text, images? }`, or `{ action: "handled" }`.
- `user_bash` runs when a user shell request is entered through interactive `!`/`!!` input or RPC `bash`. Return `{ operations }` to transform execution metadata or `{ result }` to supply a handled result.
- `session_before_switch` runs before PiSharp opens or creates a replacement session. Return `{ cancel: true, reason?: string }` to keep the current session active.
- `session_before_fork` runs before PiSharp forks a session. Return `{ cancel: true, reason?: string }` to stop the fork.
- `session_shutdown` runs before a successful replacement is applied and when the runtime is disposed. It is notification-only.

```javascript
export default function activate(pi) {
  pi.on("input", event => {
    if (event.text === "/hello") return { action: "transform", text: "say hello" };
    if (event.text === "/handled") return { action: "handled" };
    return { action: "continue" };
  });

  pi.on("session_before_switch", event => {
    if (event.targetSessionFile?.includes("blocked")) {
      return { cancel: true, reason: "blocked by extension" };
    }
  });

  pi.on("session_before_fork", event => {
    if (event.entryId === "blocked-entry") {
      return { cancel: true, reason: "blocked fork" };
    }
  });

  pi.on("session_shutdown", event => {
    console.log(`session shutting down: ${event.reason}`);
  });

  pi.on("user_bash", event => {
    if (event.command === "status") {
      return { result: { command: event.command, exitCode: 0, output: "ok", error: "" } };
    }
    return { operations: { command: `echo ${JSON.stringify(event.command)}`, excludeFromContext: true } };
  });
}
```

The `input` payload includes `text`, `images`, and `source`. The `user_bash` payload includes `command`, `excludeFromContext`, and `cwd`; result fields are `command`, `exitCode`, `output`, and `error`; operations may include `command`, `cwd`, `timeout`, and `excludeFromContext`. The first handler that returns a result or operations wins; later handlers are skipped. Hook failures are isolated and do not bypass shell/tool policy checks. Session switch payloads include `reason`, `targetSessionFile`, `currentSession`, and `targetSession`. Session fork payloads include `entryId`, `position`, `sourceSession`, and `forkOptions`. Shutdown payloads include `reason`, `targetSessionFile`, and `session`.

## UI bridge

TypeScript UI requests are proxied to the current `IExtensionUi` implementation. UI availability depends on runtime mode:

- Interactive TUI mode can provide UI methods.
- Print/RPC/non-interactive modes may not.

Extensions should tolerate unavailable UI and avoid assuming interactive prompts always work.

## Minimal TypeScript extension

```typescript
import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";
import { Type } from "typebox";

export default function (pi: ExtensionAPI) {
  pi.on("session_start", async (_event, ctx) => {
    ctx.ui.notify("Extension loaded", "info");
  });

  pi.registerTool({
    name: "greet",
    label: "Greet",
    description: "Greet someone by name",
    parameters: Type.Object({
      name: Type.String({ description: "Name to greet" })
    }),
    async execute(toolCallId, params) {
      return {
        content: [{ type: "text", text: `Hello, ${params.name}!` }],
        details: {}
      };
    }
  });
}
```

Load it with:

```bash
pisharp --extension ./my-extension.ts
```

## Limitations and trade-offs

- TypeScript extensions run out of process.
- They do not share .NET memory, dependency injection, or object identity.
- Large payloads cross JSON-RPC and can add serialization overhead. The bridge reads already-parsed `JsonElement` payloads directly on hot runtime-action paths, but payloads still cross the process boundary as JSON.
- Node.js is required.
- Bridge startup can fail independently of the .NET runtime; PiSharp records a warning and disables TypeScript extensions if startup fails.
- UI calls only work when the current runtime mode supplies an extension UI.

## Troubleshooting

| Symptom | Check |
| --- | --- |
| TypeScript extensions do not load | Verify `node` is on `PATH`. |
| Slow startup | Run with `--benchmark-startup` and inspect bridge/extension timings. |
| Cached registration is stale | Change detection uses source/dependency hashes; delete `~/.pi/PiSharp/cache/ts-bridge` to force a clean bridge cache. |
| UI request fails | Check whether the current mode has UI and whether the extension handles missing UI. |
| Tool/provider fails on first use | Descriptor replay may have succeeded but lazy activation failed; inspect bridge stderr and startup diagnostics. |
| Skill filtering has no effect | Confirm both extensions are loaded and the relevance extension appears after the embeddings extension. |
| All skills still appear | The selector fails open on missing embeddings, ranking errors, zero selected skills, and unchanged selection. Check extension stderr for `[relevance-filtered-skills]`. |
| A service consumer times out | Mark providers with `pi.extensions.declare({ provides: [key], activation: "eager" })` or call `pi.extensions.provide()` during activation. |
| Explicit `/skill:name` still works for hidden skills | Expected: relevance filtering only patches current prompt metadata and does not change the full catalog. |


## TypeScript Bridge Compatibility Shims

PiSharp keeps TypeScript bridge compatibility metadata in C#, then lets Node generate the runtime `.mjs` files that extensions import. The source of truth is `src/PiSharp.TsBridge/TsBridgeManifestFactory.cs` and the cross-process contract is `src/PiSharp.TsBridge/Protocol/TsBridgeManifestContracts.cs`.

At bridge startup, `TsExtensionHost` sends `bridgeManifest` in the `initialize` JSON-RPC payload. `Node/runner/shimGenerator.mjs` validates the manifest, materializes deterministic shim files under the bridge cache directory, and `TsBridgeRunner.mjs` rewrites known Pi package imports to those generated modules.

Currently manifest-generated shims cover:

- `@pi-ai`, `@earendil-works/pi-ai`, and `@mariozechner/pi-ai`.
- `@pi-tui`, `@earendil-works/pi-tui`, and `@mariozechner/pi-tui`.
- `@pi-coding-agent`, `@earendil-works/pi-coding-agent`, and `@mariozechner/pi-coding-agent`.

To add or change a compatibility export:

1. Update `TsBridgeManifestFactory.CreateDefault()` with the module/export metadata.
2. Prefer a structured helper in `Node/src/shims/shimGenerator.ts`; do not add ad hoc JavaScript template strings to `TsBridgeRunner.mjs`.
3. Add or update manifest tests in `tests/PiSharp.TsBridge.Tests/TsBridgeManifestTests.cs`.
4. Add a bridge integration test if the export has runtime behavior.

The manifest also includes protocol method names, runtime action names, and a JavaScript-facing API surface catalog. That catalog is the authoritative PiSharp <-> Pi parity contract, not a roadmap. Do not add false-positive entries. A member may be listed only when its status accurately describes behavior that works end-to-end:

- `implemented` means behavior is implemented in the Node bridge or generated shim without requiring a runtime callback.
- `snapshot` means the value is populated from a live runtime snapshot sent by .NET and refreshed after relevant runtime actions.
- `runtime-action` means the Node API calls `runtime_action`, `TsExtensionHost.RuntimeActionAsync` handles it, `ExtensionRuntimeBinding` exposes a delegate for it, and `RuntimeExtensionBinder` binds it to real runtime/harness behavior.

The manifest must not contain `Planned(...)`, `not-yet-supported`, stale phase labels, or unsupported reasons for JS parity APIs. `TsBridgeManifestTests.BridgeManifestDoesNotContainRoadmapOrFalseUnsupportedStatuses` enforces this.

### Runtime-action parity wiring

For every `Runtime(...)` manifest entry, update all bridge layers together:

1. Add a constant to `TsBridgeRuntimeActions` or use an existing `TsBridgeMethods` protocol method.
2. Add the action to `CreateProtocolManifest().RuntimeActions` when it is a `runtime_action`.
3. Handle the action in `TsExtensionHost.RuntimeActionAsync`.
4. Add a real delegate to `ExtensionRuntimeBinding` if runtime state is needed.
5. Bind that delegate in `RuntimeExtensionBinder` or `SessionRuntime` to live runtime behavior.
6. Expose the JavaScript wrapper in `Node/TsBridgeRunner.mjs`, `Node/runner/piApi.mjs`, or `Node/runner/uiApi.mjs`.
7. Add or update tests in `tests/PiSharp.TsBridge.Tests`.

Current runtime-action bridge coverage includes session and command control (`ctx.waitForIdle`, `ctx.newSession`, `ctx.fork`, `ctx.navigateTree`, `ctx.switchSession`, `ctx.reload`), base context lifecycle (`ctx.isIdle`, `ctx.abort`, `ctx.hasPendingMessages`, `ctx.shutdown`, `ctx.compact`, `ctx.getSystemPrompt`), root API actions (`pi.exec`, `pi.setSessionName`, `pi.setLabel`, message sending, tools, models, thinking level, resources, reload), and replacement-session message APIs.

### Runtime snapshot parity

Runtime snapshots are built in `RuntimeExtensionBinder.BuildSessionSnapshotAsync` and sent through `TsExtensionHost` to `TsBridgeRunner.mjs`. Snapshot-backed APIs must use live runtime state, not hard-coded defaults.

`RuntimeExtensionBinder` caches unchanged runtime snapshots. The cache key includes the session id/path, leaf id, model selection, scoped model list, active/all tool names, and thinking level, and it is invalidated when extension registry changes can affect tools or other runtime-visible extension state. Do not mutate returned snapshot objects in place; request a fresh snapshot after runtime actions that may change session state.

Current snapshot-backed surfaces include:

- `pi.getCommands()` and `pi.getSessionName()`.
- `ctx.sessionManager.getEntries()`, `getBranch()`, `getLeafId()`, `getLeafEntry()`, `getEntry(id)`, `getTree()`, `getChildren(parentId)`, `getLabel(id)`, `getHeader()`, `getSessionName()`, `getCwd()`, `getSessionDir()`, `getSessionId()`, `getSessionFile()`, and `isPersisted()`.
- `ctx.getContextUsage()`.
- `ctx.model` and `ctx.modelRegistry`, backed by the current runtime model selection and `PiSharp.Ai.Models.ModelRegistry`.

When adding a snapshot member, add the corresponding field to `CreateApiSurfaceManifest().RuntimeSnapshotFields`, populate it in `BuildSessionSnapshotAsync`, and expose it from the Node context without fallback stubs such as `() => true`, `() => false`, `() => undefined`, or `() => {}`.

### UI and tool metadata parity

`ctx.ui` is implemented by `Node/runner/uiApi.mjs` and forwards concrete UI requests through `ui_request` when a host UI exists. The bridge exposes editor text operations, autocomplete registration, custom components, working indicators, title updates, theme helpers including `italic`, theme getters/setters, and tools-expanded state. UI-specific extensions should still guard interactive behavior with `ctx.hasUI` or `api.HasUi` because non-interactive modes may accept requests without visible UI.

Tool descriptors preserve execution mode and dynamic metadata used by Pi-compatible extensions, including `prepareArguments` and render capability flags. Avoid broad fake compatibility stubs. If a JavaScript Pi helper is missing, implement the helper or record a deliberate non-parity decision outside the manifest until real behavior exists.

### Validation commands for bridge changes

Run these before committing TypeScript bridge parity work:

```bash
dotnet test tests/PiSharp.TsBridge.Tests/PiSharp.TsBridge.Tests.csproj
dotnet test PiSharp.sln
```

Also verify that parity stubs and false manifest statuses are absent from source files:

```bash
rg -n -e "Planned\(" -e "not-yet-supported" -e "isIdle: \(\) => true" -e "hasPendingMessages: \(\) => false" -e "getContextUsage: \(\) => undefined" -e "compact: \(\) => \{\}" -e "getSystemPrompt: \(\) => \"\"" src/PiSharp.TsBridge src/PiSharp.Extensions src/PiSharp.Runtime tests/PiSharp.TsBridge.Tests
```
