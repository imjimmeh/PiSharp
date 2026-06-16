# pi C# Port - Solution Design Document

## 1. Overview

This document describes the architecture for porting the pi-mono TypeScript project (a multi-package AI agent framework) to C# as `PiSharp`. The port preserves the full feature set, extension ecosystem, and runtime behavior while targeting .NET's performance (NativeAOT), strong typing, and ecosystem.

Key design goals:
- **Parity**: All four TS packages (pi-ai, pi-agent-core, pi-coding-agent, pi-tui) mapped to equivalent C# projects
- **Extensibility**: Dual plugin system — C# native (`AssemblyLoadContext`) and TS backwards compatibility (JSON-RPC sidecar)
- **Single binary**: NativeAOT deployment with bundled Node.js sidecar for TS extensions
- **Event-driven**: `IAsyncEnumerable<T>` as the streaming backbone, matching TS `EventStream` pattern
- **Minimal dependencies**: Prefer `Spectre.Console` for TUI, `System.Text.Json` for serialization

## 2. C# Solution Structure

```
PiSharp.sln
├── src/
│   ├── PiSharp.Abstractions/        # Core interfaces and types
│   ├── PiSharp.Agent/               # Agent loop, harness, session management
│   ├── PiSharp.Ai/                  # LLM provider abstraction + implementations
│   ├── PiSharp.Tools/               # Built-in tool implementations
│   ├── PiSharp.Extensions/          # Extension system (loading, registration, routing)
│   ├── PiSharp.Cli/                 # CLI entry point, arg parsing, mode selection
│   ├── PiSharp.Tui/                 # Terminal UI (Spectre.Console-based)
│   ├── PiSharp.TsBridge/            # TS extension bridge (JSON-RPC sidecar)
│   └── PiSharp.PluginHost/          # AssemblyLoadContext native plugin loader
├── tests/
│   ├── PiSharp.Abstractions.Tests/
│   ├── PiSharp.Agent.Tests/
│   ├── PiSharp.Ai.Tests/
│   ├── PiSharp.Tools.Tests/
│   ├── PiSharp.Extensions.Tests/
│   └── PiSharp.TsBridge.Tests/
└── tools/
    └── ts-bridge-shim/              # Node.js shim script for TS extensions
```

### 2.1 Project Responsibilities

| Project | TS Equivalent | Key Types |
|---------|---------------|-----------|
| `PiSharp.Abstractions` | `pi-ai` types + shared interfaces | `IAgentLoop`, `IAgentTool`, `IFileSystem`, `IShell`, `IExecutionEnv`, `IEventStream<TEvent, TResult>`, `IAgentEvent`, `IModelProvider`, `IModelRegistry`, `IAgentSession`, `IExtensionAPI` |
| `PiSharp.Agent` | `pi-agent-core` | `AgentLoop`, `AgentHarness`, `SessionManager`, `MessageConverter`, `Compactor` |
| `PiSharp.Ai` | `pi-ai` | `AnthropicProvider`, `OpenAIProvider`, `GoogleProvider`, `BedrockProvider`, `ModelRegistry`, `OAuthHandler` |
| `PiSharp.Tools` | `pi-coding-agent` tools | `BashTool`, `ReadTool`, `WriteTool`, `EditTool`, `GrepTool`, `FindTool`, `LsTool` |
| `PiSharp.Extensions` | `pi-coding-agent` extension system | `ExtensionManager`, `ExtensionLoader`, `ExtensionRuntime`, `EventRouter` |
| `PiSharp.Cli` | `pi-coding-agent` CLI | `Program`, `CliParser`, `RpcMode`, `InteractiveMode`, `ModelResolver` |
| `PiSharp.Tui` | `pi-tui` | `TerminalRenderer`, `EditorComponent`, `KeybindManager`, `AutocompleteHandler` |
| `PiSharp.TsBridge` | — (new) | `JsonRpcClient`, `TsExtensionProxy`, `ShimProcessManager` |
| `PiSharp.PluginHost` | — (new) | `PluginLoadContext`, `AssemblyScanner`, `SandboxPolicy` |

## 3. Core Abstractions Design

### 3.1 Agent Loop

```csharp
public interface IAgentLoop
{
    IAsyncEnumerable<IAgentEvent> ExecuteAsync(
        string prompt,
        AgentContext context,
        CancellationToken ct = default);

    Task<TResult> ExecuteWithResultAsync<TResult>(
        string prompt,
        AgentContext context,
        CancellationToken ct = default);
}
```

- `IAsyncEnumerable<IAgentEvent>` maps to TS `EventStream<AgentEvent>` — consumers `await foreach` events
- Events are discriminated by `AgentEventType` enum: `AgentStart`, `TurnStart`, `MessageStart`, `MessageUpdate`, `MessageEnd`, `ToolExecutionStart`, `ToolExecutionEnd`, etc.
- The loop follows the same lifecycle: prompt → stream → execute tools → check steering → repeat

### 3.2 Agent Tool

```csharp
[AttributeUsage(AttributeTargets.Class)]
public sealed class AgentToolAttribute : Attribute
{
    public string Name { get; }
    public string Label { get; init; }
    public string Description { get; init; }
}

public interface IAgentTool
{
    string Name { get; }
    string Label { get; }
    string Description { get; }
    string? PromptSnippet => null;
    IReadOnlyList<string> PromptGuidelines => [];
    JsonElement ParametersSchema { get; }  // JSON Schema
    ToolExecutionMode? ExecutionMode { get; }
    JsonElement PrepareArguments(JsonElement args);
    Task<AgentToolResult<object?>> ExecuteAsync(
        string toolCallId,
        JsonElement parameters,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback<object?>? onUpdate = null);
}
```

- Attribute-based discovery for C# native tools
- Tool metadata (name, label, description, parameters) available for LLM function calling schema
- Built-in typed tools derive from `JsonTool<TParameters, TDetails>` and expose `ToolSchemas.FromType<TParameters>()`.
- Tool input record properties/positional parameters use `[Description]` attributes so generated JSON Schema property descriptions stay next to the typed contract.
- TypeScript extension tools can still provide their own JSON schemas unchanged.
- Tools execute against `IExecutionEnv` abstractions for file system and shell access.

### 3.3 FileSystem / Shell / ExecutionEnv

```csharp
public interface IFileSystem
{
    Task<string> ReadAsync(string path, int? offset, int? limit);
    Task WriteAsync(string path, string content);
    Task<string[]> GlobAsync(string pattern, string? cwd);
    Task<string[]> GrepAsync(string pattern, string[] includePatterns, string? cwd);
    Task<bool> ExistsAsync(string path);
}

public interface IShell
{
    Task<ShellResult> ExecuteAsync(
        string command,
        string? workingDirectory,
        TimeSpan? timeout,
        CancellationToken ct);
}

public record ShellResult(int ExitCode, string Stdout, string Stderr);

public interface IExecutionEnv
{
    IFileSystem FileSystem { get; }
    IShell Shell { get; }
    // Session scoping, environment variables, etc.
}
```

### 3.4 Event Stream

```csharp
public interface IEventStream<TEvent, TResult>
{
    void Push(TEvent @event);
    void End(TResult result);
    void Error(Exception ex);
    
    IAsyncEnumerable<TEvent> Events { get; }
    Task<TResult> Result { get; }
}
```

- Dual consumer model: iterate events via `await foreach` and await final result
- Thread-safe `Channel<TEvent>` + `TaskCompletionSource<TResult>` under the hood
- Maps directly to TS `EventStream<TEvent, TResult>` with `push()` / `end()` / `error()` methods

### 3.5 Session Management

```csharp
public interface IAgentSession
{
    string Id { get; }
    string? ParentId { get; }
    IReadOnlyList<SessionEntry> Entries { get; }
    
    Task AppendEntryAsync(SessionEntry entry);
    Task<IAgentSession> ForkAsync(string? label);
    Task CompactAsync();
    Task SummarizeBranchAsync();
}

public interface ISessionRepo
{
    Task<IAgentSession> CreateAsync(string? parentId);
    Task<IAgentSession?> LoadAsync(string id);
    Task SaveAsync(IAgentSession session);
    Task SwitchAsync(string id);
    Task<SessionTree> GetTreeAsync();
}
```

- JSONL file format per session (matching TS `session.jsonl`)
- Session tree with fork/navigate/switch operations
- Entry types: `MessageEntry`, `ThinkingLevelChangeEntry`, `ModelChangeEntry`, `CompactionEntry`, `BranchSummaryEntry`, `CustomEntry`, `LabelEntry`, `SessionInfoEntry`, `LeafEntry`

### 3.6 Provider Abstraction

```csharp
public interface IModelProvider
{
    string Name { get; }
    IAsyncEnumerable<AssistantMessageEvent> StreamAsync(
        ModelContext context,
        StreamOptions? options,
        CancellationToken ct);
}

public interface IModelRegistry
{
    IModelProvider? GetProvider(string modelId);
    IReadOnlyCollection<ModelInfo> GetAvailableModels();
    void RegisterProvider(IModelProvider provider);
}
```

### 3.7 Extension System

```csharp
public interface IExtensionAPI
{
    void RegisterTool(ToolDefinition definition);
    void RegisterCommand(string name, CommandDefinition definition);
    void RegisterShortcut(ShortcutDefinition definition);
    void RegisterFlag(string name, FlagDefinition definition);
    void RegisterProvider(string name, ProviderDefinition definition);
    void On(string eventType, ExtensionEventHandler handler);
    Task SendMessageAsync(ExtensionMessage message);
    Task<string> ExecAsync(string command, CancellationToken ct);
    IEventBus EventBus { get; }
}

public interface IExtensionRuntime
{
    Task LoadAsync(string extensionPath, IExtensionAPI api);
    Task UnloadAsync();
    string Name { get; }
}
```

## 4. Agent Loop Design

### 4.1 Lifecycle

```
                                            ┌─────────────┐
                                            │   Prompt    │
                                            └──────┬──────┘
                                                   ▼
                                            ┌─────────────┐
                                            │  AgentInit   │
                                            │  (create ctx)│
                                            └──────┬──────┘
                                                   ▼
                                            ┌─────────────┐
                                            │ TurnStart    │
                                            │ (emit event) │
                                            └──────┬──────┘
                                                   ▼
                          ┌─────────────────────────┐
                          │  Stream from LLM         │
                          │  (IAsyncEnumerable<Event>)│
                          └──────────────┬──────────┘
                                         ▼
                    ┌─────────────────────────────────────┐
                    │ Tool call(s) detected?               │
                    │  ┌─ Sequential path                  │
                    │  │  run tools one by one             │
                    │  ├─ Parallel path                    │
                    │  │  run tools with WhenAll           │
                    │  └─ No tool call                     │
                    │     → emit message_end, go to check  │
                    └──────────────┬──────────────────────┘
                                   ▼
                    ┌─────────────────────────────────────┐
                    │ Steering message? → inject, repeat   │
                    │ Follow-up requested? → add message   │
                    │ Neither → turn_end, emit event       │
                    └──────────────┬──────────────────────┘
                                   ▼
                    ┌─────────────────────────────────────┐
                    │ Compaction needed? → compact & retry │
                    │ Otherwise → finalize result          │
                    └─────────────────────────────────────┘
```

### 4.2 Key Implementation Details

```csharp
public sealed class AgentLoop : IAgentLoop
{
    private readonly IModelProvider _provider;
    private readonly IAgentTool[] _tools;
    private readonly IExtensionManager _extensions;
    private readonly IAgentSession _session;

    public async IAsyncEnumerable<IAgentEvent> ExecuteAsync(
        string prompt, AgentContext context,
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new AgentStartEvent(...);
        
        do
        {
            yield return new TurnStartEvent(...);
            
            // Stream LLM response
            await foreach (var evt in _provider.StreamAsync(context, options, ct))
            {
                yield return evt;
            }
            
            // Check for tool calls
            if (lastMessage.HasToolCalls)
            {
                var results = await ExecuteToolsAsync(lastMessage.ToolCalls, ct);
                // Tool results feed back as new messages
            }
            
            yield return new TurnEndEvent(...);
        } while (context.HasSteering || context.HasFollowUp);
        
        yield return new AgentEndEvent(...);
    }
}

public enum ToolExecutionStrategy
{
    Sequential,  // tools run one at a time
    Parallel     // independent tools run concurrently
}
```

## 5. Extension System Design

### 5.1 Architecture

```
┌─────────────────────────────────────────────────────────┐
│                   ExtensionManager                        │
│  ┌─────────────┐  ┌─────────────┐  ┌──────────────────┐ │
│  │ NativeLoader │  │ TsBridge    │  │ EventRouter      │ │
│  │ (ALC)        │  │ (sidecar)   │  │ (middleware pipe)│ │
│  └──────┬───────┘  └──────┬──────┘  └────────┬─────────┘ │
│         │                 │                  │           │
│         ▼                 ▼                  ▼           │
│  ┌─────────────┐  ┌─────────────┐  ┌──────────────────┐ │
│  │ C# Ext .dll │  │ TS Ext .ts  │  │  AgentEvent       │ │
│  │ Assembly-   │  │ Node.js     │  │  dispatch to all  │ │
│  │ LoadContext │  │ shim process│  │  registered       │ │
│  └─────────────┘  └─────────────┘  │  handlers         │ │
│                                     └──────────────────┘ │
└─────────────────────────────────────────────────────────┘
```

### 5.2 C# Native Plugins

```csharp
public sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginPath) : base(isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginPath);
    }

    protected override Assembly? Load(AssemblyName name)
        => _resolver.ResolveAssemblyToPath(name) is { } path
            ? LoadFromAssemblyPath(path) : null;

    protected override IntPtr LoadUnmanagedDll(string name)
        => _resolver.ResolveUnmanagedDllToPath(name) is { } path
            ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
}
```

- Each native plugin loaded in its own `AssemblyLoadContext` for isolation
- `[AgentTool]` attribute scanning on load
- Extension exports an `IExtensionEntryPoint`:
  ```csharp
  public interface IExtensionEntryPoint
  {
      Task InitializeAsync(IExtensionAPI api, CancellationToken ct);
      Task ShutdownAsync(CancellationToken ct);
  }
  ```

### 5.3 Event Routing & Middleware

```csharp
public sealed class ExtensionManager : IExtensionManager
{
    private readonly List<IExtensionRuntime> _extensions = new();
    private readonly List<IAgentMiddleware> _middleware = new();
    
    public async IAsyncEnumerable<IAgentEvent> ProcessEventStream(
        IAsyncEnumerable<IAgentEvent> source,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var evt in source.WithCancellation(ct))
        {
            // Run middleware pipeline (allows interception/modification)
            var processed = await RunMiddlewareAsync(evt, ct);
            
            // Dispatch to all registered extension handlers
            await DispatchToExtensionsAsync(processed, ct);
            
            yield return processed;
        }
    }
}

public interface IAgentMiddleware
{
    Task<IAgentEvent> OnEventAsync(IAgentEvent evt, CancellationToken ct);
}
```

## 6. TS Backwards Compatibility Bridge

### 6.1 Architecture

```
┌──────────┐   JSON-RPC (stdin/stdout)   ┌──────────────┐
│ PiSharp   │ ◄────────────────────────► │ Node.js       │
│ (C#)      │                             │ ts-bridge-   │
│           │                             │ shim          │
│           │                             │               │
│           │                             │ Loads TS ext  │
│           │                             │ (via jiti)    │
└──────────┘                             └──────┬───────┘
                                                │
                                         ┌──────┴───────┐
                                         │ TS Extension  │
                                         │ (user code)   │
                                         └──────────────┘
```

### 6.2 Protocol

JSON-line protocol on stdin/stdout with message types:

```json
// C# → TS Bridge (Commands)
{"type":"register_tool","id":"1","tool":{"name":"my-tool","label":"My Tool","description":"..."}}
{"type":"register_command","id":"2","command":{"name":"mycmd","description":"..."}}
{"type":"event","eventType":"turn_start","data":{...}}
{"type":"execute_tool","id":"3","toolName":"my-tool","args":{...}}
{"type":"shutdown","id":"4"}

// TS Bridge → C# (Responses)
{"type":"result","id":"3","data":{"success":true,"output":"..."}}
{"type":"event","eventType":"tool_call","data":{...}}
{"type":"log","level":"info","message":"Extension loaded"}
```

### 6.3 Bridge Shim (Node.js Side)

The shim script:
1. Accepts extension paths as CLI args
2. Creates an `ExtensionAPI` proxy that serializes all calls as JSON-RPC
3. Loads each extension via jiti (same as TS pi-coding-agent)
4. Forwards events from C# to extensions and vice versa

### 6.4 C# Client

```csharp
public sealed class TsBridgeClient : IDisposable
{
    private readonly Process _nodeProcess;
    private readonly JsonRpcTransport _transport;
    private readonly Dictionary<string, IAgentTool> _tsTools = new();
    
    public async Task StartAsync(string[] extensionPaths, CancellationToken ct)
    {
        // Spawn Node.js with ts-bridge-shim
        var startInfo = new ProcessStartInfo("node", 
            $"ts-bridge-shim/index.js {string.Join(" ", extensionPaths)}")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        
        _nodeProcess = Process.Start(startInfo);
        _transport = new JsonRpcTransport(
            _nodeProcess.StandardOutput, 
            _nodeProcess.StandardInput);
        
        // Start reading responses
        _ = Task.Run(() => ReceiveLoopAsync(ct));
    }
    
    public async Task<AgentToolResult> ExecuteToolAsync(
        string name, JsonElement args, CancellationToken ct)
    {
        var response = await _transport.SendAsync(new
        {
            type = "execute_tool",
            toolName = name,
            args
        }, ct);
        
        return response.ToObject<AgentToolResult>();
    }
    
    public async Task DispatchEventAsync(IAgentEvent evt, CancellationToken ct)
    {
        await _transport.SendAsync(new
        {
            type = "event",
            eventType = evt.Type.ToString(),
            data = evt
        }, ct);
    }
}
```

### 6.5 ExtensionAPI Proxy (in shim)

```typescript
// Inside ts-bridge-shim/index.ts
class BridgeExtensionAPI implements ExtensionAPI {
  constructor(private transport: JsonRpcTransport) {}

  registerTool(def: ToolDefinition): void {
    this.transport.send({ type: "register_tool", tool: def });
  }

  on(eventType: string, handler: Function): void {
    this.transport.send({ type: "register_handler", eventType });
    this.transport.on(`event:${eventType}`, handler);
  }

  // All other ExtensionAPI methods serialized similarly
}
```

## 7. Provider System Design

### 7.1 Interface

```csharp
public interface ILLMProvider
{
    string Name { get; }
    string? BaseUrl { get; }
    
    IAsyncEnumerable<AssistantMessageEvent> StreamCompleteAsync(
        ChatContext context,
        StreamOptions? options,
        CancellationToken ct);
    
    Task<ModelInfo[]> ListModelsAsync(CancellationToken ct);
    bool Supports(ProviderCapability capability);  // streaming, image gen, etc.
}
```

### 7.2 Provider Registry

```csharp
public sealed class ProviderRegistry : IModelRegistry
{
    private readonly Dictionary<string, ILLMProvider> _providers = new();
    
    public void Register(ILLMProvider provider)
    {
        _providers[provider.Name] = provider;
    }
    
    public ILLMProvider Resolve(string modelId)
    {
        // Parse provider prefix: "anthropic/claude-3-opus" → AnthropicProvider
        var prefix = modelId.Split('/')[0];
        return _providers.TryGetValue(prefix, out var provider)
            ? provider
            : throw new ProviderNotFoundException(modelId);
    }
}
```

### 7.3 Provider Implementations

Each provider is a separate class implementing `ILLMProvider`:

| Provider | HTTP Client | Auth |
|----------|-------------|------|
| `AnthropicProvider` | `HttpClient` + Anthropic API | ApiKey header |
| `OpenAIProvider` | `HttpClient` + OpenAI API | ApiKey header |
| `GoogleProvider` | `HttpClient` + Google AI API | ApiKey query param |
| `BedrockProvider` | AWS SDK (`Amazon.BedrockRuntime`) | AWS credentials chain |
| `AzureProvider` | `HttpClient` + Azure OpenAI | ApiKey header |
| `OllamaProvider` | `HttpClient` + Ollama API | None (local) |
| ... | ... | ... |

All support streaming via SSE (`HttpClient.GetStreamAsync` + `JsonSerializer.DeserializeAsyncEnumerable`).

## 8. Session Storage Design

### 8.1 File Format (JSONL)

Matching TS `session.jsonl` — one JSON object per line:

```jsonl
{"type":"message","role":"user","content":"Hello","timestamp":"2026-05-21T10:00:00Z","ordinal":0}
{"type":"message","role":"assistant","content":"Hi!","timestamp":"2026-05-21T10:00:01Z","ordinal":1}
{"type":"model_change","from":"gpt-4","to":"claude-3-opus","timestamp":"2026-05-21T10:00:02Z","ordinal":2}
{"type":"compaction","originalSize":150,"compactedSize":50,"timestamp":"2026-05-21T10:00:03Z","ordinal":3}
{"type":"branch_summary","summary":"Discussed X, Y, Z","timestamp":"2026-05-21T10:00:04Z","ordinal":4}
{"type":"label","label":"fix-bug-123","timestamp":"2026-05-21T10:00:05Z","ordinal":5}
{"type":"session_info","agent":"coding-agent","model":"claude-3-opus","timestamp":"2026-05-21T10:00:00Z","ordinal":6}
{"type":"leaf","sessionId":"abc123","timestamp":"2026-05-21T10:00:10Z","ordinal":7}
```

### 8.2 Session Tree

```
MemoryRepo or FileRepo
└── Session "root" (ID: "root-uuid")
    ├── Entry 0: session_info
    ├── Entry 1: message (user)
    ├── Entry 2: message (assistant)
    │
    ├── Fork "feature-a" (ID: "fork-a-uuid")
    │   ├── Entry 0: leaf ← points to parent entry 2
    │   ├── Entry 1: message (user) "Implement X"
    │   ...
    │
    └── Fork "bug-fix" (ID: "fork-b-uuid")
        ├── Entry 0: leaf ← points to parent entry 2
        ...
```

### 8.3 Repo Implementations

```csharp
public interface ISessionRepo
{
    Task<IAgentSession> CreateAsync(string? parentId = null);
    Task<IAgentSession?> GetAsync(string id);
    Task SaveAsync(IAgentSession session);
    Task DeleteAsync(string id);
    Task<SessionTree> GetTreeAsync();
}

public sealed class FileSessionRepo : ISessionRepo
{
    private readonly string _sessionsDir;
    
    public FileSessionRepo(string sessionsDir)
    {
        _sessionsDir = sessionsDir;
    }
    
    // Session stored as: {sessionsDir}/{sessionId}/session.jsonl
    // Tree metadata: {sessionsDir}/sessions.json
}

public sealed class MemorySessionRepo : ISessionRepo
{
    private readonly Dictionary<string, IAgentSession> _sessions = new();
    // In-memory only, for testing
}
```

### 8.4 Compaction

```csharp
public sealed class Compactor
{
    public async Task<CompactionResult> CompactAsync(
        IAgentSession session, CancellationToken ct)
    {
        // 1. Summarize oldest messages up to a threshold
        // 2. Replace summarized entries with a single compaction entry
        // 3. Insert branch_summary entry
        // 4. Write back to JSONL
        // 5. Return compaction stats (originalSize, compactedSize)
    }
}
```

## 9. Tool Implementations

### 9.1 Bash Tool

```csharp
[AgentTool(Name = "bash", Label = "Bash", Description = "Execute shell commands")]
public sealed class BashTool : IAgentTool
{
    private readonly IShell _shell;
    private readonly BashOperationsHook? _customOps;
    
    public async Task<AgentToolResult> ExecuteAsync(
        JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var command = args.GetProperty("command").GetString()!;
        var timeout = args.TryGetProperty("timeout", out var t) 
            ? TimeSpan.FromSeconds(t.GetInt32()) 
            : TimeSpan.FromSeconds(30);
        var cwd = args.TryGetProperty("cwd", out var d)
            ? d.GetString() : null;
        
        // Hook: custom bash operations (from extensions)
        if (_customOps?.TryHandle(command, ctx) is { } handled)
            return handled;
        
        var result = await _shell.ExecuteAsync(command, cwd, timeout, ct);
        
        // Truncate output if too large
        var stdOut = TruncateOutput(result.Stdout);
        var stdErr = TruncateOutput(result.Stderr);
        
        return new AgentToolResult
        {
            Success = result.ExitCode == 0,
            Output = stdOut + stdErr,
            ExitCode = result.ExitCode
        };
    }
}
```

### 9.2 Edit Tool (with File Mutation Queue)

```csharp
public sealed class FileMutationQueue
{
    private readonly List<FileMutation> _pending = new();
    
    public void Enqueue(FileMutation mutation)
    {
        // Reorder mutations on the same file to maintain consistency
        // Overlapping edits are flagged for review
        _pending.Add(mutation);
    }
    
    public async Task ApplyAllAsync(IFileSystem fs, CancellationToken ct)
    {
        // Apply mutations in dependency order
        // Roll back entire batch on failure
    }
}
```

### 9.3 Tool Registry

```csharp
public sealed class ToolRegistry : IToolRegistry
{
    private readonly Dictionary<string, IAgentTool> _tools = new();
    
    public void Register(IAgentTool tool) => _tools[tool.Name] = tool;
    public IAgentTool? Get(string name) => _tools.GetValueOrDefault(name);
    public IReadOnlyCollection<IAgentTool> All => _tools.Values;
    
    public JsonElement GetFunctionSchemas()
    {
        // Build JSON array of function definitions for LLM tool calling
        var schemas = _tools.Values.Select(t => new
        {
            type = "function",
            function = new
            {
                name = t.Name,
                description = t.Description,
                parameters = t.Parameters
            }
        });
        return JsonSerializer.SerializeToElement(schemas);
    }
}
```

## 10. CLI & Mode Design

### 10.1 Entry Point

```csharp
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var parsed = CliParser.Parse(args);
        
        return parsed.Mode switch
        {
            CliMode.Interactive => await InteractiveMode.RunAsync(parsed),
            CliMode.Rpc         => await RpcMode.RunAsync(parsed),
            CliMode.Pipe        => await PipeMode.RunAsync(parsed),
            CliMode.Version     => PrintVersion(),
            _                   => PrintHelp()
        };
    }
}
```

### 10.2 RPC Mode

JSON-line protocol matching TS RPC:

```csharp
public sealed class RpcMode
{
    public async Task RunAsync(CliOptions options)
    {
        await using var bridge = options.TsExtensions.Length > 0
            ? await TsBridgeClient.StartAsync(options.TsExtensions)
            : null;
        
        var agent = CreateAgent(options, bridge);
        
        // Read commands from stdin as JSON lines
        await foreach (var command in JsonLineReader.ReadAsync(Console.In))
        {
            var response = await DispatchCommandAsync(command, agent);
            await JsonLineWriter.WriteAsync(Console.Out, response);
        }
    }
    
    private async Task<JsonElement> DispatchCommandAsync(
        RpcCommand command, AgentLoop agent)
    {
        return command.Type switch
        {
            "prompt"   => await HandlePrompt(command, agent),
            "steer"    => await HandleSteer(command, agent),
            "abort"    => await HandleAbort(command, agent),
            "bash"     => await HandleBash(command, agent),
            "fork"     => await HandleFork(command, agent),
            "compact"  => await HandleCompact(command, agent),
            _          => UnknownCommand(command.Type)
        };
    }
}
```

## 11. Deployment Design

### 11.1 NativeAOT

```xml
<PropertyGroup>
  <PublishAot>true</PublishAot>
  <StripSymbols>true</StripSymbols>
  <IlcGenerateMapFile>true</IlcGenerateMapFile>
  <IlcOptimizationPreference>Speed</IlcOptimizationPreference>
</PropertyGroup>
```

- Single executable: `publish/pi.exe` (~10-20 MB with NativeAOT)
- Self-contained, no .NET runtime dependency
- Trimming: all dynamic features (reflection) must be annotated with `[DynamicallyAccessedMembers]`

### 11.2 Node.js Sidecar

- Node.js bundled as a resource inside the executable (or alongside)
- On first TS extension use, extract + spawn
- Version-pinned Node.js (LTS) for reproducibility

### 11.3 Platform Support

| Platform | NativeAOT | TS Bridge |
|----------|-----------|-----------|
| Windows x64 | ✅ | ✅ |
| Linux x64 | ✅ | ✅ (system Node.js) |
| macOS x64/ARM64 | ✅ | ✅ |
| Windows ARM64 | ✅ | ✅ |

## 12. Key NuGet Dependencies

| Package | Purpose |
|---------|---------|
| `Spectre.Console` | TUI rendering, tables, prompts, progress |
| `System.Text.Json` | JSON serialization (ships with .NET) |
| `Microsoft.Extensions.DependencyInjection` | DI container (or use manual DI) |
| `Microsoft.Extensions.Logging` | Structured logging |
| `AWSSDK.BedrockRuntime` | AWS Bedrock provider |
| `Azure.AI.OpenAI` | Azure OpenAI provider |
| `Google.Cloud.AIPlatform.V1` | Google Vertex AI provider |

## 13. Migration Strategy

| Phase | Scope | Deliverable |
|-------|-------|-------------|
| **Phase 1** | `PiSharp.Abstractions` + `PiSharp.Agent` + `PiSharp.Tools` | Core agent loop with session management, 3 built-in tools (bash, read, write) |
| **Phase 2** | `PiSharp.Ai` | Anthropic + OpenAI providers with streaming, model registry |
| **Phase 3** | `PiSharp.Cli` + `PiSharp.Tui` | CLI entry point, RPC mode, basic interactive mode |
| **Phase 4** | `PiSharp.Extensions` + `PiSharp.PluginHost` | C# native plugin loading with ALC |
| **Phase 5** | `PiSharp.TsBridge` | TS backwards compatibility bridge |
| **Phase 6** | Remaining providers + tools | All 25+ providers, all 7 tools, OAuth support |

## 14. Risk Mitigation

| Risk | Mitigation |
|------|------------|
| NativeAOT trimming breaks reflection | Annotate with `[DynamicallyAccessedMembers]`; use source generators where possible |
| TS extensions break on Node.js version | Pin Node.js version; document minimum requirements |
| JSON-RPC bridge performance overhead | Batch events; use binary framing for large payloads; consider named pipes on Windows |
| Session format incompatibility | Version format header; provide migration tool |
| Large provider surface area (25+) | Start with top 5 (Anthropic, OpenAI, Google, Bedrock, Ollama); add others via community contributions |
