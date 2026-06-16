# EPIC-04: Built-in Tool Implementations

**Status:** Draft  
**Dependencies:** Epic 1 (Core Abstractions), Epic 2 (Agent Loop)  
**Depended On By:** Epic 5 (CLI Interface)  
**Target Project:** `PiSharp.Tools`

---

## Goal

Port all 7 built-in coding tools from TypeScript to C# with native implementations. Tools must produce byte-identical LLM output and support the same options/configuration as the TS versions.

---

## Key Deliverables

### 1. Bash Tool (`bash.ts` → `BashTool.cs`)

- Execute shell commands via `Process.Start`
- Input: `command`, `timeout`, `cwd`
- Output: `stdout`, `stderr`, `exitCode`
- Timeout handling with cancellation
- Abort via `CancellationToken`
- Streaming output updates during long-running commands
- `BashOperations` abstraction for extensibility (custom operation hooks)
- Security: command injection prevention, path sanitization
- Matching TS types: `BashToolInput`, `BashToolDetails`, `BashSpawnContext`, `BashSpawnHook`

### 2. Read Tool (`read.ts` → `ReadTool.cs`)

- Read file contents with line limits and truncation
- Input: `filePath`, `offset`, `limit`, `maxLines`, `maxBytes`
- Output: file contents prefixed with line numbers
- Truncation modes: head-only, tail-only, both ends
- Binary file detection (skip/signal non-text files)
- Large file handling (streaming reads, size limits)
- Matching TS types: `ReadToolInput`, `ReadToolDetails`, `TruncationOptions`

### 3. Write Tool (`write.ts` → `WriteTool.cs`)

- Create or overwrite files
- Input: `filePath`, `content`, `permissions`
- Auto-create parent directories
- Atomic writes (write to temp, then rename)
- Large content handling with size enforcement
- Matching TS types: `WriteToolInput`

### 4. Edit Tool (`edit.ts` → `EditTool.cs`)

- Search-and-replace text editing with context matching
- Input: `filePath`, `oldString`, `newString`, `insertLine`, `dryRun`
- Fuzzy matching when exact match fails
- Context line matching for disambiguation
- Multiple edits coordination via `FileMutationQueue` (order edits within a turn)
- Dry-run mode for preview
- Matching TS types: `EditToolInput`, `EditToolDetails`, `EditOperations`, `FileMutationQueue`

### 5. Grep Tool (`grep.ts` → `GrepTool.cs`)

- Content search using regex patterns
- Input: `pattern`, `path`, `include`, `exclude`, `maxResults`, `contextLines`
- Recursive directory traversal
- Gitignore-aware file filtering
- Binary file skipping
- Output formatting with line numbers and file paths
- Matching TS types: `GrepToolInput`, `GrepToolDetails`

### 6. Find Tool (`find.ts` → `FindTool.cs`)

- File system glob search
- Input: `pattern` (glob), `path`, `include`, `exclude`, `maxResults`
- Recursive directory traversal
- Gitignore-aware filtering
- Case-insensitive option
- Matching TS types: `FindToolInput`, `FindToolDetails`

### 7. LS Tool (`ls.ts` → `LsTool.cs`)

- Directory listing
- Input: `path`, `longFormat`, `showHidden`
- Output: file/directory list with details (name, size, modified time, permissions)
- Recursive option for subdirectory traversal
- Gitignore filtering
- Matching TS types: `LsToolInput`, `LsToolDetails`

---

## Tool Infrastructure

### 8. Tool Definition Attributes

```csharp
[AttributeUsage(AttributeTargets.Class)]
public class AgentToolAttribute : Attribute
{
    public string Name { get; }
    public string Description { get; }
    public string Label { get; }
}
```

### 9. Tool Registry (matching `tool-definition-wrapper.ts`)

- Reflection-based discovery of `[AgentTool]`-annotated classes
- Wrapping `ToolDefinition` for compatibility with extension API
- Tool option configuration at registration time

### 10. Tool Base Classes

- `AgentToolBase<TParams, TDetails>` implementing common tool patterns
- Parameter validation using JSON Schema (port TypeBox-like validation)
- Shared error handling, logging, and result formatting

---

## Tool Options

Each tool supports a configuration class mirroring its TS counterpart:

| Tool | Options |
|------|---------|
| BashTool | `BashToolOptions`: timeout, allowed commands, custom operations |
| ReadTool | `ReadToolOptions`: maxLines, maxBytes, truncation defaults |
| WriteTool | `WriteToolOptions`: maxSize, permissions |
| EditTool | `EditToolOptions`: contextLines, maxDiffs |
| GrepTool | `GrepToolOptions`: maxResults, contextLines |
| FindTool | `FindToolOptions`: maxResults, followSymlinks |
| LsTool | `LsToolOptions`: longFormat default |

---

## Implementation Notes

- Use `System.Diagnostics.Process` for bash execution (cross-platform)
- Use `StreamReader` with proper encoding for file operations
- Implement gitignore-aware file filtering (port ignore package or use a .gitignore parser library)
- All file operations must go through `IFileSystem` abstraction (not direct `System.IO`)
- Tool output must match TS format exactly for test compatibility
- Use `CancellationToken` for all long-running operations
- Implement truncation utilities matching TS patterns exactly
- Edit tool needs exact port of diff-matching algorithm for backwards compatibility
