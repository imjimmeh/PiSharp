# SDD: Subagent JSONL Mode

## Problem

Some JavaScript Pi subagent flows spawn child processes and parse newline-delimited `AgentSessionEvent` JSON from stdout.
PiSharp already has `--mode json` / `AppMode.PrintJson`, but that mode intentionally emits PiSharp's native `AgentHarnessEvent` schema, not JavaScript Pi's `AgentSessionEvent` schema.

Result: child process runs but produces unrecognizable events → "Subagent ran. It completed, but returned no summary/output."

PiSharp keeps native `--mode json` unchanged for normal print JSON output, adds an explicit JavaScript Pi compatibility mode (`--mode subagent-json`), and routes the JavaScript Pi subagent child-process invocation shape (`--mode json -p --no-session`) through the same compatibility adapter.

## Current State

### What PiSharp already has:
- `CliParser.cs`: `--mode json` → `AppMode.PrintJson`
- `CliParser.cs`: `--mode json -p --no-session` → `AppMode.SubagentJson`
- `CliParser.cs`: `--mode subagent-json` → `AppMode.SubagentJson`
- `Program.cs`: `AppMode.PrintJson` → `PrintMode.RunAsync()`
- `Program.cs`: `AppMode.SubagentJson` → `SubagentJsonMode.RunAsync()`
- `PrintMode.cs`: `RunJsonAsync()` subscribes to harness events and serializes native PiSharp JSONL.
- `SubagentJsonMode.cs`: creates a runtime subagent session and emits JavaScript Pi-compatible JSONL.
- `JsPiSubagentEventTranslator.cs` and `JsPiSubagentEventWriter.cs`: translate and write JavaScript Pi-compatible event lines.
- `StdoutGuard.cs`: provides thread-safe stdout ownership for protocol output.
- Full agent lifecycle via `SessionRuntime` + `AgentHarness`

### Current behavior:
- `--mode json` remains PiSharp-native and emits `AgentHarnessEvent` JSONL except for `--mode json -p --no-session`, which is treated as a JavaScript Pi subagent child-process compatibility invocation.
- `--mode subagent-json` emits a first `session` header line, then JavaScript Pi-compatible subagent lifecycle events.
- In-process TypeScript `createAgentSession()` uses the same runtime subagent service and event translator.

## Required JSONL Format (JS Pi Compatible)

```
{header}            ← SessionHeader (once at start)
{event}             ← AgentSessionEvent (one-per-line, streamed)
{event}
...
```

### Header Line (required first line)
```typescript
{
  type: "session",
  sessionId: string,
  sessionFile: string,
  cwd: string,
  parentSession?: string
}
```

### Event Types (must match JS Pi `AgentSessionEvent`)

Subagents extension parses these specific event types:

| Event | Key fields used |
|-------|----------------|
| `agent_start` | existence → subagent began |
| `turn_start` | existence → turn counting |
| `message_start` | `message.role`, `message.content` |
| `message_update` | `assistantMessageEvent` streaming substructure |
| `message_end` | `message` (final) |
| `tool_execution_start` | `toolName`, `args` |
| `tool_execution_end` | `toolName`, `result`, `isError` |
| `turn_end` | `message.usage`, `toolResults` |
| `agent_end` | `messages[]` — final transcript and error metadata |
| `session_info_changed` | `name` |
| `compaction_start` | `reason` |
| `compaction_end` | `result`, `aborted` |
| `auto_retry_start` | `attempt`, `errorMessage` |
| `auto_retry_end` | `success` |

### What subagents extension extracts from JSONL:

The JavaScript Pi subagent example spawns `pi --mode json -p --no-session ...`, reads JSONL line by line, and builds its result list from `message_end.message` events. It extracts final output by scanning those collected messages backward for the last `assistant` message with a `{ type: "text", text }` content part. Other subagent consumers may also inspect `agent_end.messages` for final transcript/error metadata.

## Design

### Approach

Add a new `SubagentJsonMode.cs` that creates an isolated runtime subagent session and emits JavaScript Pi-compatible JSONL to stdout. Keep existing `PrintMode.cs` and general `--mode json` unchanged because they serve PiSharp-native diagnostics and automation, but route `--mode json -p --no-session` through `SubagentJsonMode` for JavaScript Pi subagent child-process compatibility.

### Architecture

```
Program.cs
  └─ AppMode.SubagentJson → SubagentJsonMode.RunAsync()
       └─ Bootstraps PiRuntime (same as interactive)
       └─ Creates a SubagentSessionService child session
       └─ Subscribes to translated child events
       └─ Writes via StdoutGuard
```

### Implementation Plan

#### 1. New file: `src/PiSharp.Cli/Modes/SubagentJsonMode.cs`

```csharp
public static class SubagentJsonMode
{
    public static async Task<int> RunAsync(
        SessionRuntime runtime,
        SubagentJsonModeOptions options,
        IConsoleIO console,
        CancellationToken cancellationToken)
    {
        // 1. Emit session header line (first)
        // 2. Create a runtime subagent session
        // 3. Subscribe to translated child session events
        // 4. Serialize and write JSONL via StdoutGuard
        // 5. Submit prompt(s) to the child agent
        // 6. Wait for agent_end, then exit
    }
}
```

#### 2. Event translation

Map `AgentHarnessEvent` → JS Pi event JSON. Key mappings:

| PiSharp child event | JS Pi JSON line |
|---------------|-----------------|
| `Own(SessionStart)` | `{"type":"agent_start"}` |
| `Own(BeforeAgentStart)` | no equivalent in JSONL (handled by print-mode init) |
| AI response events | `{"type":"message_start","message":{...}}` |
| Streaming text/tool | `{"type":"message_update",...,"assistantMessageEvent":{...}}` |
| `Own(ToolCall)` | `{"type":"tool_execution_start","toolCallId":"...","toolName":"...","args":{...}}` |
| `Own(ToolResult)` | `{"type":"tool_execution_end","toolCallId":"...","toolName":"...","result":{...},"isError":bool}` |
| `Own(SessionInfoChanged)` | `{"type":"session_info_changed","name":"..."}` |
| Agent stop/end | `{"type":"agent_end","messages":[...]}` |
| `Own(CompactionStart)` | `{"type":"compaction_start","reason":"..."}` |
| `Own(CompactionEnd)` | `{"type":"compaction_end",...}` |

The implementation reuses the runtime subagent event translator, so CLI JSONL and TypeScript `AgentSession.subscribe()` observe the same event shapes.

#### 3. Message content translation

PiSharp uses C# types (`AssistantMessage`, `TextContent`, `Usage`, etc.) which serialize differently from JS Pi's camelCase JSON. Need a `ToSessionEventShape()` converter that produces the exact JS Pi camelCase structure.

#### 4. Thread safety

Reuse existing `StdoutGuard.TakeOver()` for thread-safe JSONL writing (already handles concurrent writes from event handlers).

#### 5. CLI wiring

```csharp
// Program.cs dispatch
AppMode.PrintJson => await PrintMode.RunAsync(...),
AppMode.SubagentJson => await SubagentJsonMode.RunAsync(...),
```

---

### Risks / Unknowns

1. **Streaming event fidelity**: PiSharp emits translated lifecycle and message events. If a future JavaScript Pi parser depends on a more specific streaming substructure, the translator may need another compatibility pass.

2. **Session header shape**: PiSharp emits `sessionId` and `sessionFile` in the compatibility header. If a downstream parser requires legacy `id` or `version` fields, add them without removing the existing fields.

3. **Tool call event timing**: PiSharp emits `ToolCall` before execution and `ToolResult` after. JS Pi emits `tool_execution_start` / `tool_execution_update` / `tool_execution_end`. We can map the start+end but streaming `update` may not be available if PiSharp runs tools synchronously.

4. **Exit code**: JS Pi CLI exits 0 on success, non-zero on error. Need to detect agent failure and return appropriate exit code.

### Implemented scope

- Shared JavaScript Pi subagent event translator and writer.
- In-process TypeScript `createAgentSession()` backed by real child sessions.
- Child prompt, steer, follow-up, abort, compact, set-model, set-thinking-level, and dispose actions.
- Explicit CLI `--mode subagent-json` compatibility mode.
- Regression tests for runtime child controls, TsBridge subagent compatibility, and CLI JSONL output.
