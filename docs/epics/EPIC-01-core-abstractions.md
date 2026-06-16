# Epic 1: Core Abstractions and Foundation

## Metadata

| Field | Value |
|-------|-------|
| **ID** | EPIC-01 |
| **Title** | Core Abstractions and Foundation |
| **Dependencies** | None (this is the foundation) |
| **Depended On By** | All subsequent epics |
| **Status** | Draft |

## Goal

Define the core interfaces and abstractions that the entire C# port depends on. This establishes the contract between all components and ensures the TypeScript architecture is faithfully translated into idiomatic C#.

## Packages / Target Projects

| Package | Description |
|---------|-------------|
| **PiSharp.Abstractions** | Shared interfaces, types, enums, and the Result pattern |
| **PiSharp.Agent.Core** | Core agent types that depend on abstractions |

## Key Deliverables

### 1. Core Type Definitions

Mapped from the TypeScript source, the following types must be defined as C# records, discriminated unions, or enums.

#### AgentMessage (Union)

A discriminated union representing all message types flowing through the agent system:

| Variant | Properties |
|---------|------------|
| `UserMessage` | Role, Content |
| `AssistantMessage` | Role, Content |
| `ToolResultMessage` | Role, ToolUseId, Content, IsError |
| `CustomMessage` | Role, Content, Metadata |

#### AgentEvent (Discriminated Union)

A discriminated union of all agent lifecycle events:

- `AgentStart`, `AgentEnd`
- `TurnStart`, `TurnEnd`
- `MessageStart`, `MessageUpdate`, `MessageEnd`
- `ToolExecutionStart`, `ToolExecutionUpdate`, `ToolExecutionEnd`

#### AgentHarnessEvent

Extends `AgentEvent` with harness-specific events:

- `QueueUpdate`
- `SavePoint`
- `Abort`
- `Settled`
- `BeforeAgentStart`
- `Context`
- `BeforeProviderRequest`
- (All other harness events from the TypeScript source)

#### AgentToolResult<T>

Generic result type for tool execution:

| Property | Type | Description |
|----------|------|-------------|
| `Content` | `T` | The tool's output content |
| `Details` | `ToolResultDetails` | Metadata about execution |
| `Terminate` | `bool` | Whether execution should terminate |

#### AgentContext

| Property | Type | Description |
|----------|------|-------------|
| `SystemPrompt` | `string` | The system-level prompt |
| `Messages` | `IReadOnlyList<AgentMessage>` | Message history |
| `Tools` | `IReadOnlyList<AgentTool>` | Available tools |

#### AgentTool<T> (Interface)

| Member | Description |
|--------|-------------|
| `Name` | Tool name |
| `Description` | Tool description |
| `Parameters` | JSON Schema for parameters |
| `Execute(T, CancellationToken)` | Executes the tool |

#### Enums

| Enum | Members |
|------|---------|
| `ToolExecutionMode` | `Sequential`, `Parallel` |
| `ThinkingLevel` | `Off`, `Minimal`, `Low`, `Medium`, `High`, `XHigh` |
| `QueueMode` | `All`, `OneAtATime` |

---

### 2. Abstraction Interfaces

#### IFileSystem

Matches the TypeScript `FileSystem` interface:

| Method | Signature |
|--------|-----------|
| `GetCwd` | `string GetCwd()` |
| `ReadTextFile` | `Task<string> ReadTextFile(string path, CancellationToken ct)` |
| `ReadTextLines` | `Task<string[]> ReadTextLines(string path, CancellationToken ct)` |
| `ReadBinaryFile` | `Task<byte[]> ReadBinaryFile(string path, CancellationToken ct)` |
| `WriteFile` | `Task WriteFile(string path, string content, CancellationToken ct)` |
| `AppendFile` | `Task AppendFile(string path, string content, CancellationToken ct)` |
| `GetFileInfo` | `Task<FileInfo?> GetFileInfo(string path)` |
| `ListDirectory` | `Task<string[]> ListDirectory(string path, CancellationToken ct)` |
| `GetCanonicalPath` | `string GetCanonicalPath(string path)` |
| `Exists` | `bool Exists(string path)` |
| `CreateDirectory` | `Task CreateDirectory(string path, CancellationToken ct)` |
| `Remove` | `Task Remove(string path, bool recursive, CancellationToken ct)` |
| `CreateTempDirectory` | `Task<string> CreateTempDirectory(CancellationToken ct)` |
| `CreateTempFile` | `Task<string> CreateTempFile(CancellationToken ct)` |
| `Cleanup` | `Task Cleanup(CancellationToken ct)` |

#### IShell

Matches the TypeScript `Shell` interface:

| Method | Signature |
|--------|-----------|
| `Exec` | `Task<ShellResult> Exec(string command, ShellOptions? options, CancellationToken ct)` |
| `Cleanup` | `Task Cleanup(CancellationToken ct)` |

Where `ShellResult` contains `Stdout`, `Stderr`, and `ExitCode`.

#### IExecutionEnv

Combines `IFileSystem` and `IShell` into a single execution environment interface.

#### IEventStream<TEvent, TResult>

Matches the TypeScript `EventStream` pattern:

| Member | Description |
|--------|-------------|
| `Push(TEvent)` | Pushes a new event |
| `End(TResult)` | Signals completion with a result |
| `ToAsyncEnumerable()` | Returns `IAsyncEnumerable<TEvent>` for consuming the stream |

#### ISessionStorage

Matches the TypeScript `SessionStorage`:

| Method | Description |
|--------|-------------|
| `GetMetadata(string sessionId)` | Retrieves session metadata |
| `GetLeafId(string sessionId)` | Gets the current leaf ID |
| `SetLeafId(string sessionId, string leafId)` | Sets the current leaf ID |
| `CreateEntryId(string sessionId)` | Creates a new entry ID |
| `AppendEntry(string sessionId, SessionTreeEntryBase entry)` | Appends an entry to the session tree |
| `GetEntry(string sessionId, string entryId)` | Gets a specific entry |
| `FindEntries(string sessionId, Func<SessionTreeEntryBase, bool> predicate)` | Finds entries matching a predicate |
| `GetLabel(string sessionId, string label)` | Gets an entry by label |
| `GetPathToRoot(string sessionId, string entryId)` | Gets the path from entry to root |
| `GetEntries(string sessionId)` | Gets all entries |

#### ISessionRepo<TMetadata>

Matches the TypeScript `SessionRepo`:

| Method | Description |
|--------|-------------|
| `Create(string id, TMetadata metadata)` | Creates a new session |
| `Open(string id)` | Opens an existing session |
| `List()` | Lists all sessions |
| `Delete(string id)` | Deletes a session |
| `Fork(string sourceId, string targetId)` | Forks a session into a new one |

#### ISession

Session abstraction providing access to metadata and leaf management:

- Metadata property
- Leaf ID get/set
- Session ID

---

### 3. Error Type Hierarchy

A typed error hierarchy using concrete classes with error code enums.

#### FileError

| Code | Description |
|------|-------------|
| `Aborted` | Operation was aborted |
| `NotFound` | File or directory not found |
| `PermissionDenied` | Access denied |
| `NotADirectory` | Path is not a directory |
| `IsADirectory` | Path is a directory (unexpected) |
| `Invalid` | Invalid path or operation |
| `NotSupported` | Operation not supported |
| `Unknown` | Unknown error |

#### ExecutionError

| Code | Description |
|------|-------------|
| `Aborted` | Execution was aborted |
| `Timeout` | Execution timed out |
| `ShellUnavailable` | Shell not available |
| `SpawnError` | Failed to spawn process |
| `CallbackError` | Error in execution callback |
| `Unknown` | Unknown error |

#### SessionError

| Code | Description |
|------|-------------|
| `NotFound` | Session not found |
| `InvalidSession` | Session is in an invalid state |
| `InvalidEntry` | Entry is invalid |
| `InvalidForkTarget` | Cannot fork to the given target |
| `Storage` | Underlying storage error |
| `Unknown` | Unknown error |

#### AgentHarnessError

| Code | Description |
|------|-------------|
| `Busy` | Harness is busy |
| `InvalidState` | Harness is in an invalid state |
| `InvalidArgument` | Invalid argument provided |
| `Session` | Session-related error |
| `Hook` | Hook execution error |
| `Auth` | Authentication error |
| `Compaction` | Compaction error |
| `BranchSummary` | Branch summary error |
| `Unknown` | Unknown error |

#### CompactionError, BranchSummaryError

Extend the base error pattern with domain-specific codes.

---

### 4. Result Pattern

A `Result<TValue, TError>` readonly struct providing a functional error-handling pattern:

```csharp
public readonly struct Result<TValue, TError>
{
    public static Result<TValue, TError> Ok(TValue value) => new(value);
    public static Result<TValue, TError> Err(TError error) => new(error);

    public bool IsOk { get; }
    public bool IsErr { get; }
    public TValue Value { get; }
    public TError Error { get; }
}
```

Static factory methods on a non-generic `Result` class:
- `Result.Ok(value)` — infers types
- `Result.Err(error)` — infers types

---

### 5. Session Tree Entry Types

#### SessionTreeEntryBase (Abstract)

| Property | Type | Description |
|----------|------|-------------|
| `Type` | `string` | Entry type discriminator |
| `Id` | `string` | Unique entry identifier |
| `ParentId` | `string?` | Parent entry ID (null for root) |
| `Timestamp` | `DateTimeOffset` | When the entry was created |

#### Specific Entry Types

- `MessageEntry`
- `ThinkingLevelChangeEntry`
- `ModelChangeEntry`
- `CompactionEntry`
- `BranchSummaryEntry`
- `CustomEntry`
- `CustomMessageEntry`
- `LabelEntry`
- `SessionInfoEntry`
- `LeafEntry`

---

## Implementation Notes

### Conventions

- All interfaces follow C# conventions: `I` prefix, `async Task` patterns, `CancellationToken` parameters.
- Discriminated unions are implemented via records (positional records for simple cases, marker base types with `OneOf` or manual pattern matching for complex cases).
- `FileError`, `ExecutionError`, and other error types are concrete classes with typed error code enums.
- The `Result` pattern is a `readonly struct` for stack allocation and performance.

### Serialization

- Use `System.Text.Json` for JSON serialization, matching the TypeScript approach.
- Custom `JsonConverter` implementations for discriminated unions and Result types where needed.

### Null Handling

- Follow the TypeScript `null`/`undefined` patterns using C# nullable reference types (`?` annotation).
- Enable nullable reference types at the project level.

### Async

- All I/O-bound methods accept `CancellationToken` as the last parameter (with a default of `default`).
- Async method naming follows the standard `Async` suffix convention.

### Dependencies

- No external dependencies for `PiSharp.Abstractions` (pure interfaces and types).
- `PiSharp.Agent.Core` depends only on `PiSharp.Abstractions`.
