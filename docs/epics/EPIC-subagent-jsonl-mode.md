# EPIC: Subagent JSONL Output Mode

**Status:** Implemented with `--mode subagent-json` and JS Pi child-process routing  
**Effort:** ~2.5 days  
**Priority:** High — blocks subagents extension from producing results in PiSharp  

---

## Summary

The subagents extension (`pi-subagents`) can spawn child Pi CLI processes and read JSONL (newline-delimited JSON) events from stdout to extract results.

PiSharp already has a `--mode json` / `AppMode.PrintJson` path, but it emits PiSharp's own `AgentHarnessEvent` schema. The subagents extension parses JS Pi's `AgentSessionEvent` schema — a completely different event structure with different field names, nesting, and event types.

**Result:** Child PiSharp processes run and complete, but produce zero recognizable events → "Subagent ran. It completed, but returned no summary/output."

This epic implemented JS Pi-compatible JSONL output through an explicit `--mode subagent-json` compatibility mode and through the JavaScript Pi subagent child-process invocation shape, `--mode json -p --no-session`. Native `--mode json` remains PiSharp's own `AgentHarnessEvent` JSONL mode for other invocations.

---

## Background Context

### How subagents works

The subagents extension (`~/.pi/agent/extensions/subagent/`) provides a `subagent` tool. When the LLM invokes it with an agent name and task, the extension:

1. **Discovers agents** from `~/.pi/agent/agents/*/agent.json` — each agent defines name, model, thinking level, tools, extensions, systemPrompt, etc.
2. **Builds CLI args** as `--mode json -p --no-session ...` in the JavaScript Pi subagent example. PiSharp recognizes that child-process shape and routes it through the JS Pi-compatible adapter while preserving native `--mode json` for other invocations.
3. **Spawns a child process** via `pi-spawn.ts:getPiSpawnCommand()` → on Windows without `pi` on PATH, this resolves to `node.exe <pi-cli-script> <args>`; with `PISHARP_CLI` env var (added in `TsExtensionHost.cs`), it spawns `dotnet exec PiSharp.Cli.dll <args>`
4. **Streams JSONL from stdout** — each line is a JSON object representing `AgentSessionEvent`
5. **Parses the stream** to extract:
   - Session header (`type: "session"`) → session file path, ID, cwd
   - Model info from `agent_end.messages[0]` → provider, model, usage
    - Final text output from collected `message_end.message` assistant text content
   - Tool execution events → args previews for progress display
   - Error detection → `stopReason === "error"` in final message

### Key reference files (source of truth)

| File | Role |
|------|------|
| `javascript/packages/coding-agent/src/modes/print-mode.ts:103-116` | JS Pi JSONL event emitter — the exact format to match |
| `javascript/packages/coding-agent/src/core/agent-session.ts:122-141` | `AgentSessionEvent` type union |
| `javascript/packages/agent/src/types.ts:403-418` | Core `AgentEvent` type union |
| `javascript/packages/ai/src/types.ts:277-290` | `AssistantMessage` shape (the most complex nested type) |
| `javascript/packages/ai/src/types.ts:347-359` | `AssistantMessageEvent` streaming substructure |
| `javascript/packages/ai/src/types.ts:254-267` | `Usage` type (token counts + cost) |
| `javascript/packages/coding-agent/src/core/session-manager.ts:29-36` | `SessionHeader` type |
| `C:\Users\jimme\AppData\Roaming\npm\node_modules\pi-subagents\src\runs\foreground\execution.ts:139-160` | How subagents builds spawn args |
| `C:\Users\jimme\AppData\Roaming\npm\node_modules\pi-subagents\src\runs\shared\pi-spawn.ts:102-115` | How subagents spawns child process |
| `C:\Users\jimme\AppData\Roaming\npm\node_modules\pi-subagents\src\runs\shared\pi-args.ts` | `buildPiArgs()` — all CLI flags passed to child |
| `C:\Users\jimme\AppData\Roaming\npm\node_modules\pi-subagents\src\shared\utils.ts` | Parsers: `getFinalOutput()`, `detectSubagentError()`, `toModelInfo()` |
| `javascript/packages/coding-agent/src/modes/rpc/jsonl.ts` | `serializeJsonLine()` / `attachJsonlLineReader()` — JSONL framing |
| `javascript/packages/coding-agent/src/core/compaction/compaction.ts:103-109` | `CompactionResult` type |
| `javascript/packages/ai/src/utils/diagnostics.ts:8-13` | `AssistantMessageDiagnostic` type |

### PiSharp reference files

| File | Role |
|------|------|
| `src/PiSharp.Cli/Program.cs:84-88` | CLI mode dispatch |
| `src/PiSharp.Cli/Parsing/CliParser.cs` | `SelectAppMode()` maps normal `--mode json` to `AppMode.PrintJson`, `--mode json -p --no-session` to `AppMode.SubagentJson`, and `--mode subagent-json` to `AppMode.SubagentJson` |
| `src/PiSharp.Cli/Parsing/CliArgs.cs:7` | `AppMode` enum |
| `src/PiSharp.Cli/Modes/PrintMode.cs:46-52` | Current `RunJsonAsync()` — harness event emitter |
| `src/PiSharp.Cli/IO/StdoutGuard.cs:21` | `WriteJsonLineAsync()` — thread-safe stdout |
| `src/PiSharp.Agent/Harness/AgentHarness.cs` | Harness event system |
| `src/PiSharp.Agent/Harness/AgentHarnessOwnEvent.cs` | Event type hierarchy |
| `src/PiSharp.Agent/Serialization/AgentJsonSerializer.cs` | JSON serialization of messages |
| `src/PiSharp.Runtime/Runtime/SessionRuntime.cs` | Runtime that wires everything |
| `src/PiSharp.Runtime/Runtime/PiRuntimeBootstrap.cs` | Startup sequence |

### Prior shim fixes (dependency chain)

Before this work, the subagents extension failed earlier in the loading chain. These were fixed:

1. **`modelRegistry.getAll()` not a function** — `RuntimeSessionSnapshot.modelRegistry` was a plain object; JS Pi uses `ModelRegistry` class with methods. Fixed in `extensionContext.ts:76` with `createModelRegistryWrapper()`.

2. **`loader.reload` not a function** — `DefaultResourceLoader` shim was empty class. Fixed by adding all `ResourceLoader` interface methods in `shimGenerator.ts:136`.

3. **`SessionManager.inMemory` not a function** — Empty class shim. Fixed with static factories and instance methods.

4. **`SettingsManager.create` not a function** — Empty class shim. Fixed with factories.

5. **`session.setSessionName` not a function** — Missing from `ctx.sessionManager` and `pi.session`. Added to both.

6. **`session.bindExtensions` not a function** — Missing from `AgentSession` shim. Added as no-op.

7. **Cache staleness** — Another worktree's builds overwrote cache. Fixed with content-hash-based cache file names.

8. **Deadlock** — `AgentSession.prompt()` called `promptAndWait` which blocked on `WaitForIdleAsync()`. Fixed by removing the wait.

9. **Wrong CLI spawned** — `getPiSpawnCommand()` spawned JS Pi instead of PiSharp. Fixed by adding `PISHARP_CLI` env var.

**Current state after all fixes:** PiSharp can emit JavaScript Pi-compatible subagent JSONL through `--mode subagent-json`, and TypeScript `createAgentSession()` uses the same runtime-owned subagent event translator in-process.

---

## Required JSONL Format

### Output structure

```
{header line}            ← SessionHeader (once, first)
{event line}             ← AgentSessionEvent (streamed, one per line)
{event line}
...
```

### Header Line (required first line)

```json
{
  "type": "session",
  "version": 3,
  "id": "abc123-def456...",       // UUID
  "timestamp": "2026-06-01T12:00:00.000Z",  // ISO 8601
  "cwd": "/home/user/project",
  "parentSession": "optional-parent-id"
}
```

### Event Types (JS Pi `AgentSessionEvent`)

The subagents extension consumes these specific events:

| Event | When emitted | Fields parsed |
|-------|-------------|---------------|
| `agent_start` | Agent loop begins | existence only |
| `turn_start` | Each LLM turn begins | existence only |
| `message_start` | Any message added to session | `message.role`, `message.content` |
| `message_update` | During streaming (assistant only) | `assistantMessageEvent` substructure |
| `message_end` | Message finalized | `message` (complete) |
| `tool_execution_start` | Tool invocation begins | `toolCallId`, `toolName`, `args` |
| `tool_execution_update` | Tool streaming partial result | `toolCallId`, `partialResult` |
| `tool_execution_end` | Tool completes | `toolCallId`, `result`, `isError` |
| `turn_end` | LLM turn finishes | `message.usage`, `toolResults` |
| `agent_end` | Agent loop ends | `messages[]` — full transcript + usage |
| `queue_update` | Pending prompt queue changes | `steering`, `followUp` |
| `session_info_changed` | Session name changes | `name` |
| `thinking_level_changed` | Thinking level changes | `level` |
| `compaction_start` | Compaction begins | `reason` |
| `compaction_end` | Compaction finishes | `result`, `aborted`, `errorMessage` |
| `auto_retry_start` | Auto-retry begins | `attempt`, `errorMessage` |
| `auto_retry_end` | Auto-retry finishes | `success`, `attempt` |

### Core data types (must match JS Pi JSON shapes exactly)

**`AssistantMessage`** (the most complex type — used in `message_start`, `message_update`, `message_end`)
```typescript
{
  role: "assistant",
  content: (TextContent | ThinkingContent | ToolCall)[],
  api: string,              // e.g. "anthropic-messages"
  provider: string,         // e.g. "anthropic"
  model: string,            // e.g. "claude-sonnet-4-20250514"
  responseModel?: string,
  responseId?: string,
  usage: Usage,
  stopReason: "stop" | "length" | "toolUse" | "error" | "aborted",
  errorMessage?: string,
  timestamp: number         // Unix ms
}
```

**`Usage`**
```typescript
{
  input: number,
  output: number,
  cacheRead: number,
  cacheWrite: number,
  totalTokens: number,
  cost: {
    input: number,
    output: number,
    cacheRead: number,
    cacheWrite: number,
    total: number
  }
}
```

**`TextContent`**: `{ type: "text", text: string }`  
**`ThinkingContent`**: `{ type: "thinking", thinking: string }`  
**`ToolCall`**: `{ type: "toolCall", id: string, name: string, arguments: Record<string, any> }`  
**`CompactionResult`**: `{ summary: string, firstKeptEntryId: string, tokensBefore: number }`

### Streaming substructure (`AssistantMessageEvent`)

Nested inside `message_update.assistantMessageEvent`:

```typescript
{ type: "start"                    , partial: AssistantMessage }
{ type: "text_start"               , contentIndex: number, partial: AssistantMessage }
{ type: "text_delta"               , contentIndex: number, delta: string, partial: AssistantMessage }
{ type: "text_end"                 , contentIndex: number, content: string, partial: AssistantMessage }
{ type: "thinking_start"           , contentIndex: number, partial: AssistantMessage }
{ type: "thinking_delta"           , contentIndex: number, delta: string, partial: AssistantMessage }
{ type: "thinking_end"             , contentIndex: number, content: string, partial: AssistantMessage }
{ type: "toolcall_start"           , contentIndex: number, partial: AssistantMessage }
{ type: "toolcall_delta"           , contentIndex: number, delta: string, partial: AssistantMessage }
{ type: "toolcall_end"             , contentIndex: number, toolCall: ToolCall, partial: AssistantMessage }
{ type: "done"                     , reason: "stop"|"length"|"toolUse", message: AssistantMessage }
{ type: "error"                    , reason: "aborted"|"error", error: AssistantMessage }
```

### Typical event stream (what a subagent child outputs)

```
{"type":"session","version":3,"id":"...","timestamp":"...","cwd":"..."}
{"type":"agent_start"}
{"type":"turn_start"}
{"type":"message_start","message":{"role":"user","content":"..."}}
{"type":"message_end","message":{"role":"user","content":"..."}}
{"type":"message_start","message":{"role":"assistant","content":[...],"model":"...","provider":"...","usage":{...},"stopReason":"..."}}
{"type":"message_update","message":{...},"assistantMessageEvent":{"type":"start",...}}
{"type":"message_update","message":{...},"assistantMessageEvent":{"type":"text_delta","delta":"..."}}
{"type":"message_update","message":{...},"assistantMessageEvent":{"type":"text_end","content":"..."}}
{"type":"message_update","message":{...},"assistantMessageEvent":{"type":"done","reason":"stop",...}}
{"type":"message_end","message":{...}}
{"type":"turn_end","message":{...},"toolResults":[...]}
{"type":"agent_end","messages":[{...},{...}]}
```

---

## Design

### Approach

Add a new `SubagentJsonMode.cs` that:
1. Uses the already-bootstrapped runtime.
2. Creates an isolated runtime subagent session.
3. Emits a JS Pi-compatible `session` header line to stdout first.
4. Subscribes to translated child session events.
5. Writes via existing `StdoutGuard` (thread-safe).
6. Submits the prompt(s) to the child session.
7. Exits 0 on success, non-zero on error.

Keep existing `PrintMode.cs` unchanged — it serves the `--mode json` use case for native PiSharp event JSONL.

### Data Flow

```
CLI flags (--mode subagent-json -p "..." --model "..." --session "..." etc.)
  ↓
CliParser → AppMode.SubagentJson
  ↓
Program.cs dispatches → SubagentJsonMode.RunAsync()
  ↓
PiRuntimeBootstrap.CreateRuntimeAsync() → SessionRuntime
  ↓
SubagentJsonMode:
  1. Emits header: { type: "session", sessionId, sessionFile, cwd }
  2. Creates child session through SubagentSessionService
  3. Subscribes to translated child AgentSessionEvent objects
  4. Writes JSONL via StdoutGuard
  5. Submits prompt via SubagentSessionService.PromptAsync()
  6. Waits for child prompt result
  7. Returns exit code
```

### Event Translation Mapping

| PiSharp AgentHarness Event | JS Pi JSONL Event |
|---------------------------|-------------------|
| `Own(SessionStart)` | `{"type":"agent_start"}` |
| AI response begin | `{"type":"turn_start"}` |
| AI assistant message | `{"type":"message_start","message":{...}}` |
| Stream chunk / update | `{"type":"message_update","message":{...},"assistantMessageEvent":{...}}` |
| Final assistant message | `{"type":"message_end","message":{...}}` |
| `Own(ToolCall)` | `{"type":"tool_execution_start","toolCallId":"...","toolName":"...","args":{...}}` |
| `Own(ToolResult)` | `{"type":"tool_execution_end","toolCallId":"...","toolName":"...","result":{...},"isError":bool}` |
| Turn complete | `{"type":"turn_end","message":{...},"toolResults":[...]}` |
| `Own(CompactionStart)` | `{"type":"compaction_start","reason":"..."}` |
| `Own(CompactionEnd)` | `{"type":"compaction_end","reason":"...","result":{...},"aborted":bool}` |
| `Own(SessionInfoChanged)` | `{"type":"session_info_changed","name":"..."}` |
| Agent done / error | `{"type":"agent_end","messages":[...]}` |
| `Own(QueueUpdate)` | `{"type":"queue_update","steering":[...],"followUp":[...]}` |

### Message Content Translation

PiSharp serializes C# types (`AssistantMessage`, `TextContent`, `ThinkingContent`, `ToolCall`, `Usage`) via `AgentJsonSerializer` which uses System.Text.Json with PascalCase or camelCase via JsonSerializerOptions. JS Pi uses camelCase exclusively.

A shared converter (`JsPiSubagentEventTranslator.cs`) produces JavaScript Pi-compatible JSON objects, including:
- `role`, `content`, `api`, `provider`, `model`, `usage`, `stopReason`, `timestamp`
- `cost` sub-object on usage
- `type: "text"`, `type: "thinking"`, `type: "toolCall"` content discriminators
- Streaming `AssistantMessageEvent` substructure on `message_update`

---

## Tasks

### Task 1: Analyze AgentHarness event stream during a prompt

**Goal:** Understand exactly which events the harness emits and when.

- Instrument `SubagentJsonMode.RunAsync()` to capture all events during a simple `dotnet run -- --mode subagent-json -p "hello"`
- Document the exact event order, types, and JSON shapes
- Compare against JS Pi's `print-mode.ts` event stream

**Output:** Event stream trace document showing gaps between PiSharp harness events and JS Pi JSONL events.

**Files to examine:**
- `src/PiSharp.Agent/Harness/AgentHarness.cs` — `Subscribe()`, event emission points
- `src/PiSharp.Agent/Harness/AgentHarnessEvent.cs` — event type hierarchy
- `src/PiSharp.Agent/Harness/AgentHarnessOwnEvent.cs` — all own-event subtypes
- `javascript/packages/coding-agent/src/modes/print-mode.ts` — JS Pi reference

### Task 2: Create JsPiSubagentEventTranslator

**Goal:** Convert `AgentHarnessEvent` → JS Pi-compatible JSON objects.

- Create `src/PiSharp.Runtime/Subagents/JsPiSubagentEventTranslator.cs`
- Implement `TranslateToJsonLine(AgentHarnessEvent)` returning `string?` (null if event should not produce output)
- Handle each event type with correct JSON shape
- Handle special cases: `agent_start`, `agent_end` (need to synthesize — may not have direct harness equivalents)

**Key mappings:**
| AgentHarnessEvent | Output |
|-------------------|--------|
| `Own(SessionStart)` | `{"type":"agent_start"}` |
| Agent completion | `{"type":"agent_end","messages":[...]}` — collect all messages during session |
| `Own(ToolCall)` | `{"type":"tool_execution_start","toolCallId":"...","toolName":"...","args":{...}}` |
| `Own(ToolResult)` | `{"type":"tool_execution_end","toolCallId":"...","toolName":"...","result":{...},"isError":bool}` |
| Streaming message | `{"type":"message_update","message":{...},"assistantMessageEvent":{...}}` |
| `Own(CompactionStart)` | `{"type":"compaction_start","reason":"..."}` |
| `Own(CompactionEnd)` | `{"type":"compaction_end","reason":"...","result":{...},"aborted":bool,"willRetry":false}` |
| `Own(SessionInfoChanged)` | `{"type":"session_info_changed","name":"..."}` |
| `Own(QueueUpdate)` | `{"type":"queue_update","steering":[...],"followUp":[...]}` |

**Files to create/modify:**
- NEW: `src/PiSharp.Runtime/Subagents/JsPiSubagentEventTranslator.cs`

### Task 3: Build SubagentJsonMode.cs

**Goal:** Wire up the event translator with runtime and stdout.

- Create `src/PiSharp.Cli/Modes/SubagentJsonMode.cs`
- Use the runtime subagent service with JS Pi event translation
- Emits session header before any other output
- Subscribes to translated child session events and writes JSONL
- Tracks message collection for `agent_end` synthesis
- Detects agent completion (harness goes idle, errors) and emits final `agent_end`
- Returns exit code based on agent result

**Files to create:**
- NEW: `src/PiSharp.Cli/Modes/SubagentJsonMode.cs`
- NEW: `src/PiSharp.Cli/Modes/SubagentJsonModeOptions.cs`

### Task 4: Wire CLI dispatch

**Goal:** Route `--mode subagent-json` to `SubagentJsonMode.RunAsync()` while preserving native `--mode json`.

- Add `AppMode.SubagentJson` and `CliMode.SubagentJson`.
- Modify `Program.cs` to dispatch `AppMode.SubagentJson` to `SubagentJsonMode.RunAsync()`.
- Keep `AppMode.PrintJson` dispatching to `PrintMode.RunAsync()`.

**Decision:** `--mode json` remains PiSharp-native. JavaScript Pi-compatible child-process output is explicit through `--mode subagent-json`.

**Files to modify:**
- `src/PiSharp.Cli/Program.cs`

### Task 5: Handle all CLI flags from subagents

**Goal:** PiSharp must accept and process the flags that `buildPiArgs()` passes.

Subagents passes these flags:
```
--mode json, -p, --model, --session, --session-dir, --task
--thinking, --tools, --extensions, --system-prompt
--no-builtin-tools, --no-extensions, --no-session
```  

PiSharp already handles most: `--mode`, `--model`, `--session`, `--thinking`, `--tools`, `--extensions`, `--system-prompt`.

**Gaps to check:**
- `--session-dir` — PiSharp may not have this flag
- `--task` — not a PiSharp flag; subagents expects it as positional arg after `-p`
- `--no-builtin-tools`, `--no-extensions`, `--no-session`
- Agent-specific `--flag` values passed via the `--flag` mechanism

**Files to review:**
- `src/PiSharp.Cli/Parsing/CliParser.cs`
- `src/PiSharp.Cli/Parsing/CliArgs.cs`
- `C:\Users\jimme\AppData\Roaming\npm\node_modules\pi-subagents\src\runs\shared\pi-args.ts`

### Task 6: End-to-end test

**Goal:** Verify subagent JSONL output matches JS Pi format.

- Write a test in `tests/PiSharp.Cli.Tests/` that:
  1. Starts PiSharp with `--mode subagent-json -p "write hello world to test.txt"`
  2. Captures stdout
  3. Parses each line as JSON
  4. Verifies:
     - First line has `type: "session"` with `id`, `cwd`, `timestamp`
     - Subsequent lines have `type` field
     - At least one `message_start` and `message_end`
     - Final event is `agent_end` with `messages` array
  5. Verifies exit code is 0

- Write a second test that sends a request that will fail (e.g., tool error) and verifies exit code is non-zero.

### Task 7: Integration test with subagents extension

**Goal:** Verify the full flow works end-to-end.

- Manual test:
  1. Load the subagents extension in PiSharp
  2. Ask the LLM to use a subagent
  3. Verify subagent runs and returns output

**Files to test:**
- `~/.pi/agent/extensions/subagent/` (the subagents extension)
- Modified `pi-spawn.ts` (the PISHARP_CLI detection)

---

## Acceptance Criteria

1. [ ] `dotnet run -- --mode subagent-json -p "hello"` emits a `session` header line first
2. [ ] All subsequent lines are valid JSON with a `type` field matching JS Pi event names
3. [ ] The `agent_end` event contains a `messages` array with at least the final assistant message
4. [ ] Assistant messages include `role`, `content`, `model`, `provider`, `usage`, `stopReason`
5. [ ] Usage includes `input`, `output`, `cacheRead`, `cacheWrite`, `totalTokens`, `cost`
6. [ ] Exit code 0 on successful agent completion
7. [ ] Exit code non-zero on agent error
8. [ ] Subagents extension can parse the output and display results in PiSharp TUI
9. [ ] All existing `PrintMode` tests continue to pass (no regression)
10. [ ] No impact on interactive mode or RPC mode

---

## Risks and Unknowns

### Risk 1: Streaming event fidelity (HIGH)

PiSharp's agent loop may not emit per-chunk `message_update` events during streaming. JS Pi emits `text_delta`, `thinking_delta`, `toolcall_delta` for each token/chunk.

**Mitigation:** If PiSharp doesn't emit granular streaming events, fall back to emitting:
- `message_start` at beginning of assistant message
- `message_end` at completion (with full message)
- Skip `message_update` entirely

The subagents extension will still get the final text output from `agent_end.messages`, but real-time progress display will be reduced to showing "running" until completion. This is acceptable for MVP.

### Risk 2: Session ID format mismatch (MEDIUM)

PiSharp session IDs use a different format than JS Pi. The `session.id` in the header must match the session IDs in subsequent events.

**Mitigation:** Expose PiSharp's session ID from `SessionRuntime` and use it consistently. If needed, generate a UUID-compatible ID.

### Risk 3: Tool execution streaming (LOW)

PiSharp runs tools synchronously — no `tool_execution_update` events exist. JS Pi supports streaming tool updates.

**Mitigation:** Map only `tool_execution_start` and `tool_execution_end`. The `update` event can be omitted. Subagents extension handles missing `update` events gracefully (it only uses `end` for progress display).

### Risk 4: Thread safety / stdout contention (LOW)

Multiple harness event handlers may fire concurrently. Writing to stdout must be serialized.

**Mitigation:** Reuse `StdoutGuard` which already provides thread-safe `WriteJsonLineAsync()`. This is proven in existing `PrintMode.RunJsonAsync()`.

### Risk 5: CLI flag compatibility (MEDIUM)

Some flags passed by subagents may not exist in PiSharp CLI parser.

**Mitigation:** Audited during Task 5. Unknown flags are captured in `CliArgs.UnknownFlags` and made available to extensions. The most critical flags (`--model`, `--session`, `--thinking`, `--tools`) are already supported.

---

## Testing Criteria

### Unit tests (`tests/PiSharp.Cli.Tests/`)

1. **`JsPiSubagentEventTranslator_ToolCall_EmitsCorrectShape`** — ToolCall event → `tool_execution_start` JSON
2. **`JsPiSubagentEventTranslator_ToolResult_EmitsCorrectShape`** — ToolResult event → `tool_execution_end` JSON  
3. **`JsPiSubagentEventTranslator_Compaction_EmitsCorrectShape`** — CompactionStart/End events
4. **`JsPiSubagentEventTranslator_SessionInfo_EmitsCorrectShape`** — SessionInfoChanged event
5. **`SubagentJsonMode_EmitsSessionHeader`** — First stdout line is valid session header
6. **`SubagentJsonMode_EmitsAgentEnd`** — Final event contains messages array
7. **`SubagentJsonMode_SuccessfulRun_ExitCodeZero`** — Happy path returns 0
8. **`SubagentJsonMode_ErrorRun_ExitCodeNonZero`** — Error path returns non-zero

### Integration tests

9. **`SubagentJsonMode_FullRun_MatchesJsPiFormat`** — Parsed JSON validates against JS Pi event types
10. **`SubagentJsonMode_WithTools_EmitsToolExecEvents`** — Tool calls produce `tool_execution_*` events

### Manual verification

11. Run `dotnet run -- --mode subagent-json -p "say hello"` and inspect stdout
12. Run subagents extension in PiSharp interactive mode and verify results display
13. Verify no regression in existing `PrintMode` behavior

### Existing tests to verify (no regression)

- `tests/PiSharp.TsBridge.Tests/` — TsBridge parity tests (108 tests, currently 106 pass)
- Any existing CLI tests

---

## References

- **SDD**: `docs/specs/SDD-subagent-jsonl-mode.md`
- **Developer guide**: `docs/pisharp-developer-guide.md`
- **Runtime guide**: `docs/pisharp-runtime.md`
- **Tools guide**: `docs/pisharp-tools.md`
- **Typescript bridge guide**: `docs/pisharp-typescript-extensions.md`
- **JS Pi reference**: `javascript/packages/coding-agent/src/modes/print-mode.ts`
- **Subagents extension**: `C:\Users\jimme\AppData\Roaming\npm\node_modules\pi-subagents\`
