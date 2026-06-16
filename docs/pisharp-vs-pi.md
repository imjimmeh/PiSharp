# PiSharp vs TypeScript Pi

PiSharp is the C#/.NET port of the original TypeScript Pi coding agent. It preserves Pi concepts and compatibility paths while adding .NET-native extension and hosting capabilities.

## Summary table

| Area | TypeScript Pi | PiSharp |
| --- | --- | --- |
| Runtime | TypeScript/Node.js packages. | C#/.NET solution targeting `net10.0`. |
| CLI binary | `pi`. | `pisharp`. |
| Core implementation | Node packages such as `pi-ai`, `pi-agent-core`, `pi-coding-agent`, `pi-tui`, and web UI packages. | .NET projects such as `PiSharp.Ai`, `PiSharp.Agent.Core`, `PiSharp.Agent`, `PiSharp.Tools`, `PiSharp.Cli`, `PiSharp.Tui`, and `PiSharp.Extensions`. |
| Extension language | TypeScript modules. | Native .NET `.dll` extensions plus compatible TypeScript extensions through a Node bridge. |
| TypeScript extension execution | Loaded directly in the Node process. | Loaded through out-of-process `TsBridgeRunner.mjs` over JSON-RPC. |
| Native plugin support | No .NET plugin host. | `.dll` plugins loaded with collectible `AssemblyLoadContext`. |
| Extension descriptor cache | Pi loads TypeScript extensions directly. | PiSharp caches TypeScript registration descriptors to reduce startup work. |
| Settings | `~/.pi/agent/settings.json` and `<cwd>/.pi/settings.json`. | Reads those plus `~/.pi/PiSharp/settings.json` and `<cwd>/.pi/PiSharp/settings.json`. |
| Settings arrays | Later settings generally replace earlier arrays. | Adds `pisharp.append` for selected arrays. |
| Sessions | Pi-compatible JSONL sessions. | Pi-compatible JSONL sessions by default, with `--no-compatibility` for PiSharp-specific leaf entries. |
| Providers | TypeScript provider registry. | .NET `IModelProvider` registry with built-in, native-extension, and TypeScript-bridge providers. |
| Tools | TypeScript tool implementations and schemas. | Built-in .NET tools plus extension tools, all exposed through `IAgentTool`. |
| Server | Separate Pi web UI/package ecosystem. | `PiSharp.Server` provides ASP.NET Core `/health` and `/ws` endpoints. |
| UI extension surface | TypeScript TUI APIs. | Native `IExtensionUi` plus bridge-mapped TypeScript UI APIs where supported. |
| Resource discovery | Pi resources under `.pi`, packages, and `resources_discover`. | Pi-compatible resources plus PiSharp-specific settings, native plugin locations, and native/TypeScript `resources_discover`. |
| Package commands | Pi package lifecycle CLI. | `pisharp install/remove/uninstall/update/list` with npm, Git, and local source support. |

## What stays compatible

PiSharp intentionally keeps these Pi concepts:

- Global agent directory under `~/.pi/agent`.
- Project directory under `<cwd>/.pi`.
- Legacy global/project settings files.
- Auth storage at `~/.pi/agent/auth.json`.
- Model catalog override at `~/.pi/agent/models.json`.
- Session root under `~/.pi/agent/sessions` by default.
- JSONL session storage.
- Pi-style TypeScript extension locations.
- Package resource conventions such as `pi.extensions`, `pi.skills`, and conventional child directories.
- JavaScript extension parity surfaces for `resources_discover`, `user_bash`, `pi.resources.list/read`, `pi.events.emit`, and TypeScript message renderers/decorators.
- Context files named `AGENTS.md` or `CLAUDE.md`.
- System prompt files at `.pi/SYSTEM.md` and `~/.pi/agent/SYSTEM.md`.
- Append prompt files at `.pi/APPEND_SYSTEM.md` and `~/.pi/agent/APPEND_SYSTEM.md`.

## PiSharp-specific additions

PiSharp adds:

- Native .NET extension assemblies.
- `PiSharp.Extensions` API for tools, commands, flags, shortcuts, providers, prompts, middleware, and UI.
- Collectible plugin loading/unloading through `PiSharp.PluginHost`.
- TypeScript extension descriptor caching under `~/.pi/PiSharp/cache/ts-bridge`.
- Native extension access to `resources_discover`, `user_bash`, chat row renderers/decorators, and the cross-extension event bus.
- PiSharp-specific settings files under `~/.pi/PiSharp` and `<cwd>/.pi/PiSharp`.
- `pisharp.append` for additive array settings.
- ASP.NET Core server project with `/health` and `/ws` endpoints.
- .NET provider registry and built-in providers.
- .NET abstractions for filesystem, shell, sessions, messages, streaming, and result types.

## Extension model differences

TypeScript Pi extensions run in the same language/runtime as Pi. PiSharp supports those extensions, but they are isolated behind the bridge:

- The sidecar is a separate Node.js process.
- Communication uses JSON-RPC over stdin/stdout.
- Registrations are mirrored into PiSharp's extension registry.
- Tool, command, provider, event, and UI calls cross the process boundary.
- Resource, user-bash, renderer/decorator, and cross-extension event calls also cross the process boundary.
- Very large payloads can be slower because they are serialized.

PiSharp keeps non-UI behavior explicit. Message renderer/decorator registrations are accepted in non-UI modes but only affect clients with a chat row rendering pipeline. TypeScript `pi.resources.read(path)` is constrained to loaded resource paths and is not a general filesystem read API.

Package command parity is intentionally practical: install, remove, uninstall, update, and list flows are available, including offline flags and self-update command parsing. Self-update reports guidance to use the host package manager. Object-form package list filters from the JavaScript ecosystem are not implemented yet.

Native PiSharp extensions are better when an extension should:

- Use .NET libraries directly.
- Register an `IModelProvider` without bridge callbacks.
- Avoid Node.js startup/runtime requirements.
- Share PiSharp abstractions and cancellation patterns.
- Participate directly in in-process prompt, event, and middleware flows.

TypeScript extensions are better when:

- Reusing existing Pi extension code.
- Depending on TypeScript/Node packages.
- Maintaining compatibility with the original Pi extension ecosystem.

## Settings differences

PiSharp settings merge order is:

1. Global legacy Pi: `~/.pi/agent/settings.json`
2. Global PiSharp: `~/.pi/PiSharp/settings.json`
3. Project legacy Pi: `<cwd>/.pi/settings.json`
4. Project PiSharp: `<cwd>/.pi/PiSharp/settings.json`

This lets existing Pi configuration continue to work while allowing PiSharp-only overrides.

The `pisharp.append` object can append to:

- `extensions`
- `skills`
- `promptTemplates`
- `themes`
- `packages`

## Session compatibility

By default, PiSharp uses Pi-compatible JSONL behavior. In compatibility mode, `JsonlSessionRepo` uses `writeLeafEntries: false`.

Use `--no-compatibility` to opt into PiSharp-specific leaf entry behavior. This is useful for PiSharp-only workflows but may reduce compatibility with tooling expecting the original Pi session shape.

## Provider differences

PiSharp model providers implement:

```csharp
public interface IModelProvider
{
    string Api { get; }

    IAsyncEnumerable<AssistantMessageEvent> StreamAsync(
        ModelDescriptor model,
        AgentContext context,
        AgentStreamOptions options,
        CancellationToken cancellationToken = default);

    Task<AssistantMessage> CompleteAsync(
        ModelDescriptor model,
        AgentContext context,
        AgentStreamOptions options,
        CancellationToken cancellationToken = default);
}
```

Built-in provider areas include Anthropic, OpenAI completions/responses, Google, Google Vertex, Bedrock, Mistral, and Faux/test provider support.

TypeScript bridge providers can still be registered by TypeScript extensions, but native providers avoid bridge callbacks.

## Migration guidance

- Keep existing Pi settings where possible; add PiSharp-specific overrides only when needed.
- Use TypeScript bridge compatibility for existing extensions first.
- Port performance-sensitive or .NET-integrated extensions to native `.dll` plugins.
- Prefer `pisharp.append` when adding PiSharp-only resources without replacing legacy Pi arrays.
- Keep compatibility mode enabled if sessions are shared with TypeScript Pi tooling.
