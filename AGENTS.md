# PiSharp Agent Guidance

This file helps coding agents work productively in PiSharp.

## Hard guardrails

- Do not modify the top-level `javascript/` directory. It is reference-only source material from the original implementation.
- Put all PiSharp implementation work in C# projects under `src/`, tests under `tests/`, and TypeScript extension work under top-level `extensions/`.
- If a request appears to require changes under `javascript/`, stop and ask for explicit confirmation before editing.

## Extension-first policy

- Treat new end-user functionality as extension-first by default.
- When adding functionality to PiSharp, implement it through extensions whenever possible (native or TypeScript), not by adding product-specific behavior directly into core/runtime/CLI/TUI projects.
- It is acceptable to extend PiSharp itself only when needed to unlock or improve extension capabilities (new extension APIs, hooks, lifecycle points, bridge features, or host infrastructure).
- After those platform extension points exist, implement the actual feature through extensions.

## Start here

- Project overview: [docs/pisharp-developer-guide.md](docs/pisharp-developer-guide.md)
- Architecture/spec context: [docs/specs/SDD-pi-csharp-port.md](docs/specs/SDD-pi-csharp-port.md)
- Runtime details: [docs/pisharp-runtime.md](docs/pisharp-runtime.md)
- Native extension details: [docs/pisharp-native-extensions.md](docs/pisharp-native-extensions.md)
- TypeScript extension bridge details: [docs/pisharp-typescript-extensions.md](docs/pisharp-typescript-extensions.md)
- Built-in tools and contracts: [docs/pisharp-tools.md](docs/pisharp-tools.md)

## Build and test commands

- Build solution: `dotnet build PiSharp.sln`
- Run all tests: `dotnet test PiSharp.sln`
- Run one test project: `dotnet test tests/PiSharp.Agent.Tests/PiSharp.Agent.Tests.csproj`
- Run targeted tests while iterating: `dotnet test tests/PiSharp.Tui.Tests/PiSharp.Tui.Tests.csproj --filter <NamePattern>`

## Architecture boundaries to preserve

- Keep `src/PiSharp.Abstractions` and `src/PiSharp.Agent.Core` as dependency-light contract layers.
- Avoid introducing dependencies from abstractions/core back into runtime, CLI, TUI, or concrete provider/tool projects.
- Keep runtime wiring in `src/PiSharp.Runtime`; avoid pushing composition concerns into feature libraries.
- Keep extension bridge/plugin host concerns inside `src/PiSharp.TsBridge` and `src/PiSharp.PluginHost`.

## Extension-specific notes

- PiSharp extension support must remain backward compatible with extensions written for the original JavaScript version.
- PiSharp may add extension functionality beyond the JavaScript version, but additions must not break existing JavaScript-compatible extension contracts.
- Native `.dll` extensions require valid metadata and a concrete `IExtension` implementation. See [docs/pisharp-native-extensions.md](docs/pisharp-native-extensions.md).
- TypeScript extensions belong in `extensions/*` and are loaded through the Node sidecar bridge, not through `javascript/packages/*`.
- Guard UI-specific extension behavior with `api.HasUi` for non-interactive modes.

## TypeScript bridge parity contract

- Treat `src/PiSharp.TsBridge/TsBridgeManifestFactory.cs` as the source of truth for PiSharp <-> JavaScript Pi TypeScript bridge parity. `CreateApiSurfaceManifest()` is a contract, not a roadmap.
- Do not add `Planned(...)`, `not-yet-supported`, stale phase labels, unsupported reasons, broad compatibility stubs, or fake helper exports for missing JavaScript Pi APIs. Implement the behavior end-to-end before declaring it in the manifest.
- For each `Runtime(...)` manifest entry, wire all layers: `TsBridgeRuntimeActions`/protocol manifest, `TsExtensionHost.RuntimeActionAsync`, `ExtensionRuntimeBinding`, `RuntimeExtensionBinder` or `SessionRuntime`, and the Node API wrapper in `TsBridgeRunner.mjs`, `runner/piApi.mjs`, or `runner/uiApi.mjs`.
- For each `Snapshot(...)` manifest entry, add the snapshot field to `RuntimeSnapshotFields`, populate it with live data in `RuntimeExtensionBinder.BuildSessionSnapshotAsync`, and expose it from the Node context without hard-coded fallbacks such as `() => true`, `() => false`, `() => undefined`, or `() => {}`.
- Current snapshot/runtime parity includes session-manager APIs, command/session control, base context lifecycle (`model`, `modelRegistry`, idle/pending/abort/shutdown/compact/system prompt), root actions such as `pi.exec`, UI helpers, and tool metadata including execution mode and argument preparation metadata.
- When changing TsBridge parity, run `dotnet test tests/PiSharp.TsBridge.Tests/PiSharp.TsBridge.Tests.csproj`; for cross-project runtime changes also run `dotnet test PiSharp.sln`.

## Known pitfalls

- Register extension flags/options before applying CLI flag values.
- Node.js availability affects TypeScript extension loading via TsBridge.
- In TUI prompt editor work, preserve PiSharp-owned keybindings behavior validated in [tests/PiSharp.Tui.Tests/TuiRenderingTests.cs](tests/PiSharp.Tui.Tests/TuiRenderingTests.cs).

## Terminal.GUI

We have creaed 3 reference documentation files for Terminal.GUI:

- [docs/terminal-gui-architecture-reference.md](docs/terminal-gui-architecture-reference.md)
- [docs/terminal-gui-examples-reference.md](docs/terminal-gui-examples-reference.md)
- [docs/terminal-gui-input-reference.md](docs/terminal-gui-input-reference.md)

Additionally, the source code for Terminal.GUI is available on the local host at:

- G:\tmp\tgui-nuget : v2.0.0 (current version used in the app at this time)
- G:\tmp\tgui : latest version as of cloning

Both version of the repository have usage examples in the `<repository_root>\Examples\UICatalog` folder.

## Locatjons

- PiSharp logs: "C:\Users\jimme\.pi\PiSharp\logs"
- PiSharp settings: "C:\Users\jimme\.pi\PiSharp\settings.json"
- Pi npm extensions: "C:\Users\jimme\.pi\agent\npm"
- Pi session files: "C:\Users\jimme\.pi\agent\sessions"
