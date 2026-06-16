# EPIC-12 JavaScript Extension Parity Audit

**Date:** 2026-06-01
**Scope:** Remaining parity surfaces between the JavaScript reference implementation and PiSharp.
**Reference:** `javascript/packages/coding-agent/` is reference-only and must not be modified.

## Package Commands And Aliases

The JavaScript CLI exposes package lifecycle commands as top-level commands before normal prompt/runtime startup:

| Command | Alias | Notes |
| --- | --- | --- |
| `install <source>` | none | Adds a package source; `--local`/`-l` targets project settings. |
| `remove <source>` | `uninstall` | Removes by parsed package identity; `--local`/`-l` targets project settings. |
| `update` | none | Updates Pi and extensions by default. |
| `update pi` / `update self` / `update --self` | none | Targets the Pi executable/self-update path. |
| `update --extensions` | none | Updates configured extension packages. |
| `update --extension <source>` / `update <source>` | none | Updates a specific extension package identity. |
| `list` | none | Lists configured user and project packages. |

Unknown package options and missing option values are reported as CLI parse errors, not as user prompt text.

## Source Parsing And Identity Matching

Source parsing is prefix-driven:

- `npm:<package>[@version]` is an npm source. The identity strips the version and uses the package name.
- `git:<url>[@ref]`, `https://...`, and `ssh://...` are git sources. The identity strips the ref and normalizes to host/repository path.
- Relative or absolute filesystem paths are local sources. Local identities resolve against the selected settings base.
- Inputs such as `git@github.com:user/repo` and `github.com/user/repo` without a `git:` prefix are treated as local paths by the JavaScript parser.

Pinned npm versions and pinned git refs are excluded from broad extension updates.

## Install Roots And Settings Persistence

The JavaScript package manager keeps installed package assets under managed roots and stores source configuration in settings:

- User-scope packages persist to user settings and install under user package roots.
- Project-scope packages persist to `.pi/settings.json` and install under project package roots such as `.pi/npm` or `.pi/git`.
- Local packages are not copied; the persisted path is relative to the relevant settings base where possible.
- Removal matches by parsed identity rather than raw input string.
- When listing configured packages, project entries win over user entries for the same identity.

**Residual gap:** Object-form package filters are not implemented. The JavaScript CLI allows filtering listings with object-shaped parameters (e.g., `pi.packages.list({ layer: 'user' })`). PiSharp's `IPackageCommandRunner.ListAsync()` returns all entries without filter support. This is documented in `EPIC-12-js-extension-parity-remaining.md`.

Destructive git update operations are scoped to managed package directories only.

## `resources_discover` Payload And Result

JavaScript extensions register a handler with `pi.on("resources_discover", handler)`.

Payload shape:

```json
{
  "cwd": "<current working directory>",
  "reason": "startup|reload"
}
```

Result shape:

```json
{
  "skillPaths": ["./skills"],
  "promptPaths": ["./prompts"],
  "themePaths": ["./themes"]
}
```

All handler results are aggregated. Handler failures are reported and later handlers still run. Results contribute resource paths only; extensions themselves are not reloaded from these returned paths.

## `user_bash` Payload And Result

JavaScript extensions register a handler with `pi.on("user_bash", handler)`.

Payload shape:

```json
{
  "command": "git status",
  "excludeFromContext": false,
  "cwd": "<current working directory>"
}
```

Result shape:

```json
{
  "operations": {},
  "result": {
    "command": "git status",
    "exitCode": 0,
    "output": "...",
    "error": ""
  }
}
```

The first handler returning a non-empty result wins. Throwing handlers are isolated: the error is reported and dispatch continues until a result is found or all handlers are exhausted.

## `pi.events.emit/on` Ordering And Failure Isolation

The JavaScript event bus exposes `pi.events.on(channel, handler)` and `pi.events.emit(channel, payload)`.

- Handlers run in registration order.
- `on` returns a disposer that unregisters the handler.
- Throwing or rejected handlers are reported without stopping later handlers.
- The bus can be cleared/reset between extension host lifetimes.
- Event delivery is cross-extension by channel, not limited to the emitting extension.

## `registerMessageRenderer` Custom-Message Semantics

JavaScript extensions register custom message renderers by custom message type string:

```javascript
pi.registerMessageRenderer("custom-card", (message) => ({
  lines: [`card:${message.data.title}`]
}));
```

The registration is not a row-type renderer by default; it applies to custom-message entries whose custom type matches the registered string. Renderer failures should fall back to built-in or generic custom row rendering rather than breaking the TUI render pipeline.

## Implemented PiSharp Surface: TypeScript `resources.list/read`

PiSharp's Node API exposes `pi.resources.list()` and `pi.resources.read(path)`, and the C# runtime now handles `list_resources` and `read_resource` actions against the loaded resource set.

The implementation exposes only loaded PiSharp resources and does not turn `resources.read` into a general filesystem read capability.
