# PiSharp Runtime, Settings, Resources, CLI, and Sessions

This document covers how PiSharp starts, where it reads configuration and resources, how CLI modes are selected, and how sessions are stored.

## Startup sequence

`PiRuntimeBootstrap.CreateRuntimeAsync()` builds a `SessionRuntime`. At a high level it:

1. Loads layered settings with `PiSettingsStore`.
2. Registers built-in model providers and loads `models.json` if present.
3. Resolves base extension, skill, prompt-template, theme, package, context, and prompt resources with `PiResourceLoader`.
4. Loads initial prompt templates and the first matching TUI theme.
5. Resolves or creates the current JSONL session.
6. Creates built-in tools and selects active tools.
7. Discovers and initializes native `.dll` extensions.
8. Starts the TypeScript bridge if TypeScript extension paths exist.
9. Applies registered extension CLI flags.
10. Dispatches extension `resources_discover` and merges contributed skill, prompt-template, and theme paths.
11. Reloads prompt templates/themes when discovery adds matching paths.
12. Resolves provider, model, and thinking-level selection.
13. Builds system-prompt options from tools, custom prompts, context files, docs paths, and loaded skills.
14. Creates the system prompt composer, including extension prompt contributors, sections, and transforms.
15. Creates an `AgentHarness<TMetadata>` bound to the session, model API, tools, prompt options, and extension registry.
16. Dispatches a `session_start` event.

The resulting `SessionRuntime` owns the session repo/current session, harness, extension manager, native plugin host, optional TypeScript host, settings snapshot, selected model, resources, loaded skills, prompt templates, theme, and startup diagnostics. Runtime snapshot reads are cached while the session leaf, model selection, tool set, thinking level, and extension-visible runtime state remain unchanged.

## Settings layers

PiSharp reads both legacy Pi settings and PiSharp-specific settings. Later layers override earlier layers:

1. Global legacy Pi: `~/.pi/agent/settings.json`
2. Global PiSharp: `~/.pi/PiSharp/settings.json`
3. Project legacy Pi: `<cwd>/.pi/settings.json`
4. Project PiSharp: `<cwd>/.pi/PiSharp/settings.json`

Settings currently include:

- `defaultProvider`
- `defaultModel`
- `defaultThinking`
- `sessionDir`
- `extensions`
- `skills`
- `promptTemplates`
- `themes`
- `packages`
- `noExtensions`
- `noSkills`
- `noPromptTemplates`
- `noThemes`
- `noContextFiles`
- `offline`

## Appending array settings

Normally later array settings replace earlier arrays. PiSharp adds a `pisharp.append` escape hatch for selected arrays:

```json
{
  "pisharp": {
    "append": {
      "extensions": ["./tools/my-extension.dll"],
      "skills": ["./.pi/skills"]
    }
  }
}
```

Appendable arrays are:

- `extensions`
- `skills`
- `promptTemplates`
- `themes`
- `packages`

Appended values are de-duplicated case-insensitively.

## Important paths

`PiAgentPaths.FromCwd()` derives these paths:

| Data | Default path |
| --- | --- |
| Global Pi agent directory | `~/.pi/agent` |
| Project Pi directory | `<cwd>/.pi` |
| Global legacy settings | `~/.pi/agent/settings.json` |
| Project legacy settings | `<cwd>/.pi/settings.json` |
| Global PiSharp directory | `~/.pi/PiSharp` |
| Project PiSharp directory | `<cwd>/.pi/PiSharp` |
| Global PiSharp settings | `~/.pi/PiSharp/settings.json` |
| Project PiSharp settings | `<cwd>/.pi/PiSharp/settings.json` |
| Auth storage | `~/.pi/agent/auth.json` |
| Model catalog override | `~/.pi/agent/models.json` |
| Keybindings | `~/.pi/agent/keybindings.json` |
| Default sessions root | `~/.pi/agent/sessions` |
| Current-project sessions directory | `~/.pi/agent/sessions/--<encoded-cwd>--` |
| TypeScript bridge cache | `~/.pi/PiSharp/cache/ts-bridge` |

The encoded current working directory trims leading path separators and replaces `/`, `\`, and `:` with `-`, then wraps the result in `--`.

## OAuth login flow

`/login <provider>` and `--login <provider>` store credentials in `~/.pi/agent/auth.json`.

For OAuth providers, PiSharp prints the authorization URL and attempts to open it in the default browser. Browser launch failures are non-fatal because the URL remains visible in the terminal.

OpenAI Codex OAuth listens on `http://localhost:1455/auth/callback` to match the registered redirect URI. If the browser callback does not arrive before the local callback wait expires, PiSharp asks for a pasted authorization code or full redirect URL as a manual fallback.

## Session directory precedence

PiSharp chooses the session root in this order:

1. CLI `--session-dir`
2. Runtime `SessionsRoot`
3. `sessionDir` setting
4. Default `~/.pi/agent/sessions`

## Sessions

PiSharp stores sessions as JSONL through `JsonlSessionRepo` and `JsonlSessionStorage`.

Startup supports:

- New session creation.
- `--continue` / `-c` for the latest session in the current working directory.
- `--resume` / `-r` alias behavior at CLI level.
- `--session <id-or-path>` to open by id or path.
- `--fork <id-or-path>` to fork from an existing session.
- `--no-session` for in-memory operation.
- `--session-dir <path>` for a custom root.

In compatibility mode, `JsonlSessionRepo` is configured with `writeLeafEntries: false`. Passing `--no-compatibility` enables PiSharp-specific leaf entry behavior.

## Resource discovery

`PiResourceLoader` resolves resources from settings, CLI flags, packages, and conventions.

### Extensions

When extensions are enabled, PiSharp loads:

- Global TypeScript/JavaScript extensions from `~/.pi/agent/extensions/*.ts` and `*.js`.
- Global TypeScript/JavaScript extensions from `~/.pi/agent/extensions/*/index.ts` and `index.js`.
- Project TypeScript/JavaScript extensions from `<cwd>/.pi/extensions/*.ts` and `*.js`.
- Project TypeScript/JavaScript extensions from `<cwd>/.pi/extensions/*/index.ts` and `index.js`.
- Paths from `settings.extensions`.
- Paths from CLI `--extension` / `-e`.
- Package resources from `pi.extensions` or package `extensions/`.
- Native `.dll` extensions from explicit `.dll` extension paths.
- Native `.dll` extensions discovered under `<cwd>/plugins` and `<cwd>/.pi/extensions`.

Disable with `--no-extensions` or `noExtensions`.

For debugging, `--no-resources` also disables extensions as part of a single resource-loading umbrella switch.

### Skills

When skills are enabled, PiSharp loads skill directories from:

- `~/.pi/agent/skills`
- `~/.agents/skills`
- Each ancestor's `.agents/skills`
- Each ancestor's `.pi/skills`
- `settings.skills`
- CLI `--skill`
- Package resources from `pi.skills` or package `skills/`

Disable with `--no-skills` or `noSkills`.

For debugging, `--no-resources` also disables skills.

### Prompt templates and themes

Prompt templates come from settings, CLI, and package resources. Disable with `--no-prompt-templates` or `noPromptTemplates`.

Themes come from settings, CLI, and package resources. Disable with `--no-themes` or `noThemes`.

For debugging, `--no-resources` disables both prompt templates and themes.

### Extension resource discovery

After native and TypeScript extensions are activated, but before final skills, prompt templates, and themes are composed, PiSharp dispatches `resources_discover` with:

- `cwd` — current working directory.
- `reason` — currently `startup` for runtime bootstrap discovery.

Handlers may return `skillPaths`, `promptPaths`, and `themePaths`. PiSharp merges those paths into the loaded resource set with deterministic de-duplication. It does not reload or discover new extensions from contributed paths.

When prompt or theme paths are contributed, PiSharp rebuilds the prompt-template catalog and theme document from the merged path set before constructing `SessionRuntime`. Skill paths are loaded downstream from the merged set.

TypeScript `pi.resources.list()` exposes loaded concrete resource files, and `pi.resources.read(path)` can read only paths returned by the loaded resource set. Unknown or arbitrary paths fail instead of becoming filesystem reads.

### Context and prompt files

Context files named `AGENTS.md`, `AGENTS.MD`, `CLAUDE.md`, or `CLAUDE.MD` are discovered in the global agent directory and every ancestor of the current working directory. Disable with `--no-context-files` or `noContextFiles`.

For debugging, `--no-resources` also disables context-file loading.

### Resource-debug shortcut

`--no-resources` is a CLI-only convenience flag that expands to the existing resource disable switches:

- `--no-extensions`
- `--no-skills`
- `--no-prompt-templates`
- `--no-themes`
- `--no-context-files`

It does not disable tools. Use it when you want to isolate PiSharp runtime behavior from resource-loaded content without changing tool availability.

Preferred system prompt files:

- Project: `<cwd>/.pi/SYSTEM.md`
- Global: `~/.pi/agent/SYSTEM.md`

Preferred append prompt files:

- Project: `<cwd>/.pi/APPEND_SYSTEM.md`
- Global: `~/.pi/agent/APPEND_SYSTEM.md`

Project prompt files win over global prompt files.

## CLI mode selection

`pisharp` chooses mode from CLI args and stdin:

- `--mode rpc` -> RPC mode.
- `--mode json` -> print JSON mode. With `-p --no-session`, this routes to the JavaScript Pi-compatible subagent JSONL mode used by spawned subagent child processes.
- `--mode subagent-json` -> JavaScript Pi-compatible subagent JSONL mode.
- `--mode text` -> print text mode.
- `--print` / `-p` -> print text mode.
- Redirected stdin -> print text mode.
- Otherwise -> interactive TUI mode.

`--mode json` keeps PiSharp's native `AgentHarnessEvent` JSONL shape except for the JavaScript Pi subagent child-process invocation shape: `--mode json -p --no-session`. That discriminator routes to the same compatibility adapter as `--mode subagent-json`, emitting JavaScript Pi `AgentSessionEvent` JSONL with a first `session` header line and streamed lifecycle events.

## Subagent sessions

PiSharp supports two subagent adapters backed by the same runtime-owned subagent service:

- TypeScript extensions call `createAgentSession()` through `PiSharp.TsBridge` and receive an in-process `AgentSession` proxy.
- CLI child processes can use `--mode subagent-json`, or the JavaScript Pi-compatible `--mode json -p --no-session` invocation, to emit JavaScript Pi-compatible JSONL on stdout.

Each subagent session creates an isolated child `SessionRuntime` view and `AgentHarness`. Child prompts, steering, follow-ups, abort, compaction, model changes, thinking-level changes, and disposal are routed by child `sessionId` and do not mutate the parent harness queue.

Subagent lifecycle events are translated to JavaScript Pi `AgentSessionEvent` objects. The same translator feeds TypeScript `AgentSession.subscribe()` listeners and the CLI JSONL compatibility mode.

In-process TypeScript subagent events are delivered asynchronously. The bridge batches short event bursts per child session, preserves per-session order, and sends them to Node as notifications rather than awaited request/response calls. This prevents slow JavaScript listeners from backpressuring the child harness. Extension code should treat `AgentSession.subscribe()` as an event stream and not assume `AgentSession.prompt()` waits for every subscriber callback to complete.

Main-session TypeScript event forwarding uses a similar background queue for ordinary non-mutating harness events. Startup `session_start` and mutating hooks that return decisions still run through awaited dispatch paths so runtime behavior remains deterministic.

## Common CLI flags

```text
pisharp [options] [prompt]

-h, --help                   Show help.
-v, --version                Show version.
--mode <text|json|rpc|subagent-json>
                              Select output mode.
--provider <name>            Select provider.
--model <model>              Select model/provider-model.
--api-key <key>              Provide API key override.
-p, --print [prompt]         Run print mode.
-t, --tools <a,b>            Restrict active tools.
--no-tools, -nt              Disable all tools.
--no-builtin-tools, -nbt     Disable built-in tools.
-e, --extension <path>       Load extension path.
--no-resources               Disable resource-loaded extensions, skills, prompt templates, themes, and context files.
--no-extensions, -ne         Disable extensions.
--skill <path>               Add skill path.
--theme <path>               Add theme path.
--prompt-template <path>     Add prompt-template path.
--no-context-files, -nc      Disable context files.
--benchmark-startup          Print startup timings to stderr.
```

Additional parsed flags include `--system-prompt`, `--append-system-prompt`, `--thinking`, `--continue`, `--resume`, `--no-session`, `--session`, `--fork`, `--session-dir`, `--models`, `--login`, `--logout`, `--reload`, `--no-compatibility`, `--list-models`, `--offline`, and `--verbose`.

Unknown long flags are captured and can be claimed by extensions through `RegisterFlag()`.

## Troubleshooting

| Symptom | Check |
| --- | --- |
| Settings not taking effect | Confirm layer precedence and whether a later layer overwrote the value. |
| Array setting unexpectedly replaced | Use `pisharp.append` for additive `extensions`, `skills`, `promptTemplates`, `themes`, or `packages`. |
| Extension flag rejected | Ensure the extension registers the flag before CLI flag application. |
| Session not continuing | Check the selected session root and current working directory encoding. |
| Context file missing | Confirm the file is named `AGENTS.md` or `CLAUDE.md` and is in the global agent directory or a cwd ancestor. |
| Startup is slow | Run `--benchmark-startup` to inspect settings, resources, extension, prompt, and session timings. |

For "start as bare as possible, but keep tools", begin with `--no-resources` before stacking more specific debug flags.
