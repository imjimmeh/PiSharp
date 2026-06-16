# PiSharp Agent Coordination

PiSharp Agent Coordination is a native .NET extension that lets multiple PiSharp agents running in the same repository discover each other, exchange messages, and receive soft conflict warnings when agents edit the same files without re-reading.

## Architecture

A repo-local **coordination daemon** owns per-repository state over a Windows named pipe (`pisharp-coordination-<guid>`). The first PiSharp agent in a repo starts the daemon in-process; subsequent agents connect to the existing daemon. Daemon metadata lives under `<repo>/.pi/coordination/` and is guarded by a file-based lock (`daemon.lock`).

The **coordination extension** is a native `.dll` plugin (`PiSharp.Coordination.dll`) that registers tools, prompt hooks, event handlers, and tool middleware with the PiSharp extension API.

```
Terminal A (PiSharp)          Terminal B (PiSharp)
  │                             │
  │  CoordinationExtension      │  CoordinationExtension
  │  (in-process)               │  (in-process)
  │        │                    │        │
  │        ▼                    │        ▼
  │  DaemonConnection           │  DaemonConnection
  │        │                    │        │
  └────────┼────────────────────┼────────┘
           │     Named Pipe     │
           ▼                    ▼
      CoordinationDaemon (in-process, owned by first agent)
           │
           ▼
      CoordinationJsonlStore
      (<repo>/.pi/coordination/events.jsonl)
```

## Building and installing

### Build

Build the solution, which includes the coordination project:

```bash
dotnet build PiSharp.sln
```

The output assembly is `src/PiSharp.Coordination/bin/Debug/net10.0/PiSharp.Coordination.dll`.

### Install

Install the built DLL globally:

```bash
pisharp install src/PiSharp.Coordination/bin/Debug/net10.0/PiSharp.Coordination.dll
```

This copies the DLL to `~/.pi/extensions/`, so it is discovered on later PiSharp starts.

Install it only for the current repo with `--local`:

```bash
pisharp install src/PiSharp.Coordination/bin/Debug/net10.0/PiSharp.Coordination.dll --local
```

This copies the DLL to `<repo>/.pi/extensions/`. Use `--force` to replace an existing installed DLL.

Or pass it explicitly at startup:

```bash
pisharp --extension src/PiSharp.Coordination/bin/Debug/net10.0/PiSharp.Coordination.dll
```

### Daemon startup

The daemon starts automatically when the first PiSharp agent in a repo initializes the coordination extension. No separate process launch is needed.

Daemon metadata and logs live under `<repo>/.pi/coordination/`:

| Path | Purpose |
| --- | --- |
| `daemon.json` | Active daemon lease (process id, pipe name, timestamps) |
| `daemon.lock` | File-based mutual exclusion lock (auto-deleted on release) |
| `events.jsonl` | Append-only audit log of all coordination events |

## Tools

The extension registers three tools:

### `coordination_roster`

Lists all agents known to the coordination daemon. Returns a markdown list with agent id, process id, working directory, and registration timestamp.

Parameters: none required.

### `coordination_send`

Sends a message to another agent or all agents.

| Parameter | Type | Required | Description |
| --- | --- | --- |
| `to` | string | no | Target agent id. Defaults to `"all"` to broadcast. |
| `body` | string | yes | Message content. Max 8192 characters. |

### `coordination_inbox`

Checks for incoming messages.

| Parameter | Type | Default | Description |
| --- | --- | --- |
| `includeRead` | bool | `false` | Whether to include previously seen messages. |
| `limit` | int | `20` | Maximum number of messages to return, from 1 to 100. |

The inbox tracks a read cursor per agent. By default (`includeRead: false`), only messages newer than the last inbox read are returned. Use `includeRead: true` to re-read all messages.

## Prompt brief

The extension registers a `before_prompt_render` handler that appends a **Coordination Brief** section to the system prompt before each turn. The brief is appended to the `"instructions"` slot with section id `"pisharp.coordination.brief"` and includes:

- **Known Agents**: Agents registered with the daemon, including replayed records from prior daemon starts.
- **Unread Messages**: Messages received since the last inbox check.

The brief is only injected when there is content to report. Daemon failures during brief generation are caught silently and do not crash prompt rendering.

## File activity tracking

The extension uses tool middleware to track file reads and writes across agents:

- **Reads** (tool `read`): Recorded before the tool executes.
- **Writes** (tools `write`, `edit`, `apply_patch`): Recorded after a successful tool execution.

The middleware intercepts tool calls, parses file paths from tool arguments using `FileToolActivityParser`, and records normalized paths to the daemon. Paths are normalized relative to the agent's working directory.

## Soft conflict warnings

Before a write tool executes, the middleware requests a **preflight check** from the daemon. The daemon's `SoftConflictDetector` warns when:

1. The current tool is a write (`write`, `edit`, or `apply_patch`).
2. Another agent has written to the same file **after** this agent's most recent read of that file.
3. The warning hasn't already been acknowledged by a newer re-read or repeated write attempt.

When a conflict is detected, the middleware sets `context.Blocked = true` and supplies a `BlockReason` message telling the model:

> Warning: agent '<id>' edited '<path>' at <timestamp> — after your last read. re-read the file before editing, and re-run the tool only if the edit is still safe.

The block is a **soft block** (not a hard error). The next turn can re-attempt the write; if the same write intent is repeated after a re-read, or no newer conflict exists, the preflight allows it.

The preflight only applies to write operations. Read operations pass through without checks.

## Subagent event observation

The extension observes lifecycle events from `@tintinweb/pi-subagents` (or compatible subagent systems) by listening for these event names on the extension event bus:

- `subagents:created`
- `subagents:started`
- `subagents:completed`
- `subagents:failed`
- `subagents:steered`
- `subagents:compacted`

Each event is mapped to a `SubagentObservedRecord` through `PiSubagentsEventAdapter` and sent to the daemon. Captured fields include `id`, `type`, `description`, `status`, `durationMs`, `toolUses`, `inputTokens`, `outputTokens`, `parentSessionId`, and `cwd`.

Field values are truncated to safe limits (type: 256 chars, description: 1024 chars, status: 64 chars) before storage.

## Known limits

### `isolated: true` subagents

Subagents running with `isolated: true` do not share the main agent's extension event bus. The coordination extension cannot observe their lifecycle events or track their file activity. Only the main agent and non-isolated subagents participate in coordination.

### `extensions: false` subagents

Subagents with `extensions: false` cannot register or use the coordination tools since they don't load extensions at all. They are invisible to the roster and cannot send or receive messages.

### Single-repo scope

Coordination is scoped to a single repository root. Agents in different repos (or different working directories outside the same repo) use separate daemons and do not see each other.

### In-process daemon

The daemon runs in-process within the first PiSharp agent. If that agent exits, the daemon stops. Subsequent agents will start a new daemon. Existing state is preserved in the JSONL store and replayed by the new daemon, but messages sent while the daemon was down are lost.

### No persistent message queue

Messages sent to an agent that is not currently connected are stored in the daemon's in-memory state and persisted to JSONL. They are available to the target agent when it connects or reconnects, but the daemon has no delivery-guarantee mechanism beyond the JSONL replay at startup.

### Cross-platform

The daemon uses `NamedPipeServerStream`/`NamedPipeClientStream`, which is .NET's cross-platform pipe transport. Verify non-Windows daemon behavior before production use.

## `.pi/agents/<name>.md` frontmatter

The coordination extension is compatible with existing `@tintinweb/pi-subagents` installations. To configure an agent for direct participation, add a coordination section to its markdown agent definition:

```yaml
---
name: my-agent
model: claude-sonnet-4-5
tools:
  - coordination_roster
  - coordination_send
  - coordination_inbox
instructions: |
  You can use coordination tools to communicate with other agents in this repo.
  Check coordination_roster to see who is available.
  Use coordination_send to send messages, and coordination_inbox to check for replies.
  If you get a soft conflict warning, re-read the file before editing.
---
```

The agent definition file goes in `<repo>/.pi/agents/<name>.md`. This is the conventional `@tintinweb/pi-subagents` location for agent definitions; `pi-subagents` reads these files when launching subagents that participate in coordination.
