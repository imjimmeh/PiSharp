## Epic 6: Extension System and TypeScript Bridge

### Dependencies: Epics 2, 4, 5 (Agent Loop, Tools, CLI)
### Depended On By: None (final layer)

### Goal
Build the C# extension system that supports both native C# plugins (via AssemblyLoadContext + attributes) AND backwards compatibility with existing TypeScript extensions (via out-of-process JSON-RPC bridge to Node.js). Also implement the middleware pipeline for event interception.

### Target Projects: PiSharp.Extensions, PiSharp.TsBridge, PiSharp.PluginHost

### Key Deliverables

1. **C# Native Extension System** (`extensions/` → `ExtensionManager.cs`):
   - `IExtension` interface:
     ```csharp
     public interface IExtension
     {
         Task InitializeAsync(IExtensionAPI api, CancellationToken ct);
         Task ShutdownAsync();
     }
     ```
   - Extension discovery:
     - Attribute-based: scan assemblies for [Extension] attribute
     - Config-based: extension paths in .pi/config.json
     - Convention-based: `extensions/` directory scanning
   - `ExtensionManager`: loads, initializes, and manages extension lifecycle
   - Extension isolation: each extension loaded in its own AssemblyLoadContext
   - Hot-reload support: reload extensions without restart

2. **C# Extension API** (`ExtensionAPI.cs`):
   - `IExtensionAPI` interface matching TS ExtensionAPI:
     - `On<TEvent>(string eventType, Func<TEvent, ExtensionContext, Task> handler)` event subscription
     - `RegisterTool<TParams, TDetails>(ToolDefinition)` tool registration
     - `RegisterCommand(name, options)` command registration
     - `RegisterShortcut(keyId, options)` keyboard shortcut registration
     - `RegisterFlag(name, options)` CLI flag registration
     - `RegisterProvider(name, config)` provider registration
     - `UnregisterProvider(name)` provider removal
     - `SendMessage(message, options)` message queueing
     - `SendUserMessage(content, options)` user message queueing
     - `AppendEntry<T>(customType, data)` session entry appending
     - `SetSessionName(name)` / `GetSessionName()`
     - `SetLabel(entryId, label)` 
     - `Exec(command, args)` shell execution
     - `GetActiveTools()` / `SetActiveTools(toolNames)`
     - `GetAllTools()` / `GetCommands()`
     - `SetModel(model)` / `GetThinkingLevel()` / `SetThinkingLevel(level)`
     - `Events` property for EventBus access

3. **Event Routing System**:
   - Event bus with publish/subscribe pattern
   - Event type routing: AgentEvent → ExtensionManager → all registered handlers
   - Handler result aggregation (e.g., multiple handlers can modify before_agent_start result)
   - Async event processing with proper ordering
   - Error isolation: one handler's failure doesn't crash others

4. **Middleware Pipeline** (`Middleware/`):
   - `IAgentMiddleware` interface:
     ```csharp
     public interface IAgentMiddleware
     {
         Task OnEventAsync(AgentEventContext context, CancellationToken ct);
     }
     ```
   - Middleware chain: events flow through middleware before reaching extensions
   - Middleware can: observe, modify, block, or replace events
   - Built-in middleware examples:
     - Security middleware (block dangerous tool calls)
     - Audit middleware (log all events)
     - Rate-limiting middleware
     - Custom header injection middleware
   - Registration: `pi.UseMiddleware<T>()` in startup

5. **TypeScript Extension Bridge** (`TsBridge/`):
   - **C# Host Side** (`TsExtensionHost.cs`):
     - Spawns Node.js child process with TS bridge shim
     - JSON-RPC protocol over stdin/stdout
     - Discovers TS extensions from config paths
     - Forwards C# agent events to TS sidecar
     - Receives TS tool registrations and routes tool calls back to TS
     - Manages sidecar lifecycle (start, restart, shutdown)
   
   - **TS Bridge Shim** (TypeScript, shipped with C# app):
     - Lightweight Node.js script
     - Loads TS extensions using existing extension loader code
     - Implements JSON-RPC server over stdio
     - Routes RPC method calls to ExtensionAPI methods
     - Forwards TS-registered tools/events/commands back to C# host
   
   - **Protocol Messages**:
     ```csharp
     // C# → TS: Tool registration from TS extension
     { "method": "tool_registered", "params": { "name": "...", "schema": {...} } }
     
     // TS → C#: Execute a TS-registered tool
     { "method": "execute_tool", "params": { "name": "...", "args": {...} }, "id": "1" }
     
     // C# → TS: Event forwarding
     { "method": "event", "params": { "type": "turn_start", "data": {...} } }
     
     // TS → C#: Extension calls pi.sendMessage()
     { "method": "send_message", "params": { "customType": "...", "content": "...", ... } }
     ```

6. **Plugin Host** (`PluginHost/`):
   - `AssemblyLoadContext`-based plugin isolation
   - Plugin scanning: `plugins/` directory for `.dll` files
   - Plugin metadata: name, version, description from assembly attributes
   - Plugin dependencies: automatic resolution from `plugins/` subdirectory
   - Plugin unloading: support for unloading and reloading
   - Security: plugin permission boundaries

7. **Extension Context** (`ExtensionContext.cs`):
   - `IExtensionContext` matching TS ExtensionContext:
     - UI access (ExtensionUIContext)
     - hasUI flag
     - cwd
     - SessionManager access
     - ModelRegistry access
     - Current model
     - isIdle / signal / abort
     - hasPendingMessages
     - shutdown
     - getContextUsage / compact / getSystemPrompt
   - `ExtensionCommandContext` extending with session control methods

### TS Extension Compatibility Testing
Critical tests to validate backwards compatibility:
1. Load existing TS extension that registers tools → verify tools appear in C# agent
2. Load TS extension with event handlers → verify events fire correctly
3. Load TS extension with commands → verify commands work in CLI
4. Load TS extension with provider registration → verify provider is usable
5. Extension lifecycle: reload, shutdown, error recovery

### Implementation Notes
- JSON-RPC implementation: System.Text.Json with streaming for large payloads
- Node.js process management: proper cleanup, timeout, error recovery
- TS bridge must support all 25+ event types from the TS ExtensionAPI
- Each event must be bidirectionally mapped between C# and TS formats
- The bridge should be optional: C# host works without Node.js if no TS extensions are configured
- Plugin unloading in .NET is limited - design for appdomain-level isolation
- Middleware pipeline should be ordered (global before extension-specific)
- Event handler exceptions should be caught and logged, not crash the agent

## Carry-over from Epic 5 CLI/modes

Epic 5 establishes protocol seams for images and extension UI but intentionally does not implement the extension runtime bridge. Epic 6 owns the remaining work:

- Wire `ImageContent` from print/RPC/TUI prompt inputs through provider payload construction instead of treating it as a DTO-only seam.
- Implement RPC `extension_ui_response` handling and the corresponding pending-request registry.
- Add TUI dialogs/widgets for extension UI prompts, confirmations, working indicators, and widget lifecycle events.
- Add extension-owned command discovery for `get_commands` and TUI autocomplete suggestions.
- Add end-to-end tests proving extension UI traffic never writes human-readable logs to RPC stdout.
