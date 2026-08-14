---
name: extension-platform
description: >
  Use when extending PiSharp through extensions: native .dll extensions,
  TypeScript extensions via the Node sidecar, the shared IExtensionApi surface
  (Settings/State/Tools/Skills/Packages/Model/Events/Prompt/Completion/Search/
  Urls/Rules/Telemetry/Ui), extension discovery, lifecycle, or adding a native
  extension/plugin (the extension-first workflow).
type: cross-cutting
scope:
  - src/PiSharp.Extensions/**
  - src/PiSharp.PluginHost/**
  - src/PiSharp.TsBridge/**
  - src/PiSharp.Runtime/**
  - docs/pisharp-native-extensions.md
  - docs/pisharp-typescript-extensions.md
related_skills:
  - tsbridge-parity
  - plugin-portfolio
  - tools-and-commands
  - repository-overview
last_verified:
  commit: "646522ccc6edc48acc39e4545cd120af9f1dafba"
  date: "2026-08-14"
confidence: high
---

# Extension Platform and Lifecycle

## When to use this skill

Use this skill when:

- adding a native `.dll` extension or TypeScript extension;
- changing extension discovery or loading;
- extending `IExtensionApi` or the extension event surface;
- registering tools/commands/shortcuts/flags/providers from an extension;
- understanding how native and TypeScript extensions share one registry;
- adding a plugin to the portfolio (the add-native-extension workflow).

Typical tasks include:

- creating a new net10.0 class library implementing `IExtension`;
- wiring a new `IExtensionApi` surface member;
- changing extension load/unload behavior (collectible ALC);
- registering a tool or slash command from an extension.

Do not use this skill for:

- the TsBridge parity manifest contract — use [tsbridge-parity](../tsbridge-parity/SKILL.md);
- a specific shipped plugin's internals — use [plugin-portfolio](../plugin-portfolio/SKILL.md).

## Responsibilities and boundaries

This area owns:

- the extension contract (`IExtension`, `IExtensionApi`, `ExtensionManager`);
- native extension loading (collectible ALC) and unload;
- TypeScript extension loading via the Node sidecar bridge;
- the shared registry where both paths register surface.

This area does not own:

- the parity manifest (TsBridgeManifestFactory) — tsbridge-parity;
- product-specific plugin implementations — plugin-portfolio;
- built-in (non-extension) tools — tools-and-commands.

## Architecture

Extensions are the extension-first delivery vehicle for new end-user
functionality. Two load paths exist, but both register through one
`ExtensionManager` surface:

- **Native .dll**: loaded by `PiSharp.PluginHost` in a collectible
  `AssemblyLoadContext` (unloadable); discovered from plugin directories.
- **TypeScript**: loaded through the Node sidecar bridge in `PiSharp.TsBridge`;
  the Node wrapper (`piApi.mjs`) exposes the Pi API to scripts.

`ExtensionManager` builds an `ExtensionRuntimeBinding` (carrying `Cwd`, `HasUi`,
`Ui`, `Session`) and hands each extension an `IExtensionApi` façade over it.
Removal is keyed on `EffectiveSourceId`.

### Important components

| Component | Location | Responsibility |
|---|---|---|
| Extension API | `src/PiSharp.Extensions/IExtensionApi.cs` | Full surface handed to extensions |
| Extension manager | `src/PiSharp.Extensions/ExtensionManager.cs` | Registry for both load paths; `ExtensionManagerParameters(Cwd, HasUi, IExtensionUi Ui, SendMessageAsync, GetSessionNameAsync)` |
| Extension events | `src/PiSharp.Extensions/ExtensionEvents.cs` | Event name constants (`ThinkingLevelSelect`, `SettingsChanged`, `ResourcesUpdate`, `AdvisorNote`, `PackagesChanged = "extensions_changed"`, `SkillsChanged = "skills_changed"`, `SkillExecutionStart`, `SkillExecutionEnd`) |
| Plugin host | `src/PiSharp.PluginHost` | Collectible ALC loading of native extensions |
| TsBridge | `src/PiSharp.TsBridge` | Node sidecar + `TsBridgeRunner.mjs`, `runner/piApi.mjs`, `runner/uiApi.mjs` |
| Runtime binding | `src/PiSharp.Runtime/RuntimeExtensionBinder.cs` | Binds runtime actions to the extension surface |
| Runtime bootstrap | `src/PiSharp.Runtime/PiRuntimeBootstrap.cs` | `CreateRuntimeAsync`, `LoadExtensionsIntoAsync` |
| Extension reload | `src/PiSharp.Runtime/RuntimeExtensionReloader.cs` | `ReloadAsync` via `ExtensionLoadCoordinator` |

### Main flow

1. `PiRuntimeBootstrap.CreateRuntimeAsync` composes the runtime with the
   extension manager and plugin host.
2. `LoadExtensionsIntoAsync` discovers and loads extensions.
3. Native extensions load in the plugin host's collectible ALC; TypeScript
   extensions load through the Node sidecar bridge.
4. Each extension receives an `IExtensionApi` façade; it registers tools,
   commands, shortcuts, flags, providers, and event handlers.
5. `RuntimeExtensionReloader.ReloadAsync` (via `ExtensionLoadCoordinator`)
   invalidates and reloads extensions on demand.

## Project terminology

| Term | Meaning in this repository |
|---|---|
| Extension | A native `.dll` or TypeScript add-on implementing `IExtension` |
| Plugin | A shipped native extension in the P07-P31 portfolio |
| ALC | `AssemblyLoadContext`; plugin host uses a collectible one for unload |
| TsBridge | The Node sidecar bridge hosting TypeScript extensions |
| EffectiveSourceId | Identity key used to remove/replace an extension registration |
| Scoped settings | Namespace-prefixed extension settings (`extensions.<ns>.`) |

## Important entry points
- [`skills/SKILL.md`](../../SKILL.md): project router — routing index for all PiSharp project skills.


- [`src/PiSharp.Extensions/IExtensionApi.cs`](../../../src/PiSharp.Extensions/IExtensionApi.cs):
  the full extension surface.
- [`src/PiSharp.Extensions/ExtensionManager.cs`](../../../src/PiSharp.Extensions/ExtensionManager.cs):
  registration/removal semantics.
- [`src/PiSharp.PluginHost`](../../../src/PiSharp.PluginHost): native loading.
- [`docs/pisharp-native-extensions.md`](../../../docs/pisharp-native-extensions.md):
  native extension authoring.
- [`docs/pisharp-typescript-extensions.md`](../../../docs/pisharp-typescript-extensions.md):
  TypeScript extension authoring.

## Dependencies and consumers

### Depends on

- `src/PiSharp.Extensions`, `src/PiSharp.Agent.Core`, `src/PiSharp.Abstractions`
  (extension projects may reference these; they must NOT reference
  Runtime/Cli/Tui).

### Consumed by

- The Node sidecar (`piApi.mjs`) and shipped plugins (plugin-portfolio).
- The daemon for hot-reload commands (`install_extension`, `update_extension`,
  `remove_extension` — see [daemon-protocol](../daemon-protocol/SKILL.md)).

### External systems

- Node.js (TypeScript extension loading via TsBridge).
- NuGet (extension package installs).

## Invariants

The following must remain true:

1. Native and TypeScript extensions register through the same `ExtensionManager`
   surface — one registry, two load paths.
2. Extension projects depend only on `Extensions`/`Agent.Core`/`Abstractions`;
   never on Runtime/Cli/Tui.
3. Native extensions must have valid metadata and a concrete `IExtension`
   implementation (`[ExtensionMetadata("id")]`).
4. Extension support remains backward compatible with extensions written for the
   original JavaScript version.
5. UI-specific extension behavior is guarded by `HasUi` for non-interactive modes.
6. Extension removal is keyed on `EffectiveSourceId`.

## Common change workflows

### Add a native extension/plugin

Use this process when adding a new plugin to the portfolio.

1. Create a net10.0 class library under `src/` named after the plugin
   (e.g. `src/PiSharp.MyPlugin`), referencing `PiSharp.Extensions`,
   `PiSharp.Agent.Core`, `PiSharp.Abstractions` — never Runtime/Cli/Tui.
2. Implement `IExtension` with `[ExtensionMetadata("id")]`; register your
   surface (tools, commands, shortcuts, flags, providers, events) through the
   `IExtensionApi` you are given.
3. Add a matching test project under `tests/` (see
   [local-development](../local-development/SKILL.md) for the test command pattern).
4. Add the project to `PiSharp.sln`.
5. Document the plugin in `docs/pisharp-plugins.md` and add a status row in
   `docs/pisharp-implementation-status.md`.

Files commonly changed together:

- `src/<Plugin>/*.csproj`, `src/<Plugin>/*.cs`
- `tests/<Plugin>.Tests/<Plugin>.Tests.csproj`
- `PiSharp.sln`
- `docs/pisharp-plugins.md`
- `docs/pisharp-implementation-status.md`

Validation:

```bash
dotnet build PiSharp.sln
dotnet test tests/<Plugin>.Tests/<Plugin>.Tests.csproj
```

### Extend IExtensionApi

Use this process when adding a capability extensions can call.

1. Add the member to `IExtensionApi` and its implementation in the
   `ExtensionManager`-provided façade.
2. If the capability has an event, add the constant to `ExtensionEvents.cs`.
3. If TypeScript extensions must see it, wire the TsBridge parity layers (see
   [tsbridge-parity](../tsbridge-parity/SKILL.md)) — manifest, runtime action or
   snapshot, Node wrapper.
4. Update `docs/pisharp-native-extensions.md` / `docs/pisharp-typescript-extensions.md`.

Files commonly changed together:

- `src/PiSharp.Extensions/IExtensionApi.cs`
- `src/PiSharp.Extensions/ExtensionManager.cs`
- `src/PiSharp.Extensions/ExtensionEvents.cs`
- `src/PiSharp.Runtime/RuntimeExtensionBinder.cs`

Validation:

```bash
dotnet build PiSharp.sln
dotnet test tests/PiSharp.Extensions.Tests/PiSharp.Extensions.Tests.csproj
dotnet test tests/PiSharp.TsBridge.Tests/PiSharp.TsBridge.Tests.csproj
```

## Testing and validation

Run for all changes in this area:

```bash
dotnet build PiSharp.sln
```

Run conditionally:

```bash
dotnet test tests/PiSharp.Extensions.Tests/PiSharp.Extensions.Tests.csproj
dotnet test tests/PiSharp.TsBridge.Tests/PiSharp.TsBridge.Tests.csproj
dotnet test PiSharp.sln
```

## Operational considerations

- Extension discovery paths include plugin directories under the working
  directory and extension directories under the Pi home
  (`~/.pi/extensions`); exact user-specific paths are environment-dependent —
  verify against the running settings before assuming a fixed list.
- TypeScript extensions require Node.js to be available.

## Common mistakes

- Do not reference Runtime/Cli/Tui from an extension project.
- Do not forget `[ExtensionMetadata("id")]` — native extensions require valid
  metadata plus a concrete `IExtension` implementation.
- Do not assume `HasUi` is true in non-interactive modes; guard UI-specific
  behavior with it.
- Do not hand-wire TsBridge parity for a new surface member without updating the
  manifest contract — see tsbridge-parity.
- Do not make extension loading dependent on `settings.json` contents that are
  user-specific and unverifiable in the repository.

## Legacy and deprecated patterns

- Original JS Pi loaded extensions from `javascript/packages/*`; PiSharp loads
  TypeScript extensions through the Node sidecar bridge instead. The
  `javascript/` directory is reference-only.

## Existing authoritative documentation

- [`docs/pisharp-native-extensions.md`](../../../docs/pisharp-native-extensions.md)

  * Covers native extension requirements and authoring.
  * Treat as authoritative for the native path.
  * Does not cover the full `IExtensionApi` member list — verify against
    `IExtensionApi.cs`.

- [`docs/pisharp-typescript-extensions.md`](../../../docs/pisharp-typescript-extensions.md)

  * Covers the TypeScript path through the Node sidecar.
  * Treat as authoritative for the TS path.

- [`AGENTS.md`](../../../AGENTS.md)

  * Extension-first policy and the native-extension notes.

## Known ambiguity and technical debt

- The exact extension discovery directory list is partly environment-specific
  (user home based) and was not fully verifiable from the repository alone.
- Collectible ALC unload semantics are load-bearing for hot reload; verify
  unload behavior when changing plugin host internals.

## Evidence and verification

This skill was verified against commit `646522ccc6edc48acc39e4545cd120af9f1dafba`.

Primary evidence:

- [`src/PiSharp.Extensions/IExtensionApi.cs`](../../../src/PiSharp.Extensions/IExtensionApi.cs)
- [`src/PiSharp.Extensions/ExtensionManager.cs`](../../../src/PiSharp.Extensions/ExtensionManager.cs)
- [`src/PiSharp.Extensions/ExtensionEvents.cs`](../../../src/PiSharp.Extensions/ExtensionEvents.cs)
- [`src/PiSharp.Runtime/RuntimeExtensionBinder.cs`](../../../src/PiSharp.Runtime/Runtime/RuntimeExtensionBinder.cs)
- [`src/PiSharp.Runtime/PiRuntimeBootstrap.cs`](../../../src/PiSharp.Runtime/Runtime/PiRuntimeBootstrap.cs)
- [`docs/pisharp-native-extensions.md`](../../../docs/pisharp-native-extensions.md)
