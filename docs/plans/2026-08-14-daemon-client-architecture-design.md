# Design: Daemon + Event-Sourced Client Architecture

Date: 2026-08-14
Status: Approved (design phase)

## Context

`pisharp` today is a monolith: every invocation builds a `SessionRuntime` in-process and wires the
TUI directly into the runtime (`TuiHostOptions` is constructed from `runtime.Harness` and a wall of
delegates in `InteractiveMode.CreateTuiHostOptions`). Every run pays full startup cost (extension
loading, TypeScript bridge, resource discovery, session scan); long turns block the UI; and only one
terminal can talk to a given runtime at a time.

A standalone `PiSharp.Server` (ASP.NET Core WebSocket host) already exists with API-key auth, a
`ServerSessionRegistry`, a session command set, and per-session sequence-numbered event streaming —
but nothing connects to it today.

This design splits the app into a per-user **daemon** (backend) and a **TUI client** (frontend) that
communicate over WebSocket using an **event-sourced** protocol: the client reconstructs all UI state
by replaying and applying a sequence-numbered event stream.

## Goals / Non-goals

Goals:
- Eliminate per-run startup cost: the daemon keeps runtimes warm; the TUI opens instantly.
- Keep the TUI responsive during turns via event-driven, batched rendering (no polling).
- Support multiple live sessions and multiple terminals attaching to the same live session.
- Full feature parity for the TUI (chat, prompt editor, slash commands, session switcher/resume/
  fork, extension UI bridge, shortcuts).

Non-goals (v1):
- Non-interactive CLI modes (`print`, `json`, `subagent-json`, `rpc`) stay in-process/stdio.
  Subagent child processes stay in-process (one-shot, must not depend on daemon availability).
- Remote (non-localhost) connections.

## Decisions (from Q&A)

1. Motivation: all of startup cost, responsive TUI, multi-session/multi-terminal.
2. Transport: reuse the existing WebSocket server (evolve `PiSharp.Server` into the daemon).
3. Default CLI: `pisharp` (interactive) becomes a client and auto-starts the daemon when absent.
4. Session lifecycle: live sessions keep running after disconnect and support re-attach.
5. Client scope: full current TUI experience.
6. Daemon scope: one per user, lease at `~/.pi/PiSharp/daemon.json`.
7. Architecture: **event-sourced client** — client rebuilds all UI state from a replayable,
   sequence-numbered event stream with resync support.

## Architecture

```
┌─────────────────────────────┐        WebSocket (ws://127.0.0.1:<port>/ws)
│  pisharp daemon (detached)  │◄───────────────────────────────┐
│                             │                                │
│  ASP.NET Core host (Sdk.Web)│        ┌──────────────────────┐ │
│  - PiServerHost             │        │  pisharp (TUI client)│ │
│  - ServerSessionRegistry    │        │                      │ │
│  - LiveServerSession x N    │  events│  RemoteTuiBackend    │ │
│    ├─ retained event log    │────────►  ClientSessionState  │ │
│    └─ sinceSequence replay  │        │  (event-sourced)     │ │
│  - AgentRuntime x N (warm)  │        │  TuiHost / Terminal  │ │
│  - Extensions (native+TS)   │  cmds  │                      │ │
│  - Theme/prompts/resources  │◄────────  (create/attach,     │ │
│                             │        │   prompt, steer, ...)│ │
└─────────────────────────────┘        └──────────────────────┘
```

### Components

- **Daemon** — one per user. Hosts all live sessions and warm `SessionRuntime`s. Runs detached,
  survives client exit. Same binary as the CLI: a new `daemon` mode (`pisharp daemon
  start/stop/status`) hosting the server wiring. `PiSharp.Server` is refactored so the host wiring
  lives in a library class `PiServerHost`; `Program.cs` becomes thin.
- **Client** — `pisharp` in interactive mode. Checks the lease; if no live daemon, spawns one
  detached and waits on `/health`. Connects via WebSocket with the api key.
- **Sessions** — daemon owns sessions; client attaches to a `LiveServerSession` by runtime session
  id (`--attach <id>` or daemon-remembered last-active). Live session disposed only after an idle
  timeout (default 5 min) with zero attached clients. Multiple clients can attach to the same live
  session, each with its own replay cursor.

### Wire protocol

Transport: WebSocket at `ws://127.0.0.1:<port>/ws`, api-key header validated (existing
`ApiKeyValidator`). JSON per frame; `request_id` correlates command → response (reuses
`ServerCommandEnvelope.Id` / `ServerResponse.Id`).

Commands (daemon responds; existing set kept, extended):
- Keep: `create_session`, `dispose_session`, `prompt`, `steer`, `follow_up`, `queue_next_turn`,
  `abort`, `get_state`, `get_messages`, `list_sessions`, `set_model`, `set_thinking_level`,
  `compact`, `new_session`, `switch_session`, `fork`, `set_session_name`.
- Add:
  - `attach { sessionId, sinceSequence }` — replay retained log from `sinceSequence`, then live.
  - `run_command` / `complete_command` (slash commands + completion; ported from RPC mode).
  - `process_input` (user input hooks incl. bash `!`) → `TuiInputHookResult`.
  - `get_theme`, `get_session_snapshot` (metadata + branch entries), `get_fork_messages`,
    `get_extension_load_status`, `get_extension_shortcuts`, `get_extension_registry`,
    `resolve_tool`, `cycle_thinking_level`.
  - `get_startup_messages` + `post_startup_checks` (npm outdated) — or pushed as first events on attach.
  - `get_available_models`, `get_commands`, `get_last_assistant_text` (ported from RPC mode).

Command flow: `run_command`, `prompt`, `compact`, `process_input` execute server-side inside
`RunExclusiveAsync`; responses carry the synchronous result; the event stream carries the async
lifecycle (`turn_start`, `message_*`, `tool_*`, `compaction_*`, …).

Events (the event-sourced backbone): one `ServerEventEnvelope` per event —
`{ Type:"event", ServerSessionId, Sequence (monotonic, per session), Timestamp, Event: AgentSessionEvent }`.
Flat JS-compatible shape, serialized with the existing `AgentJsonSerializer`.

Replay: `LiveServerSession` gains a retained in-memory ring buffer (e.g., last 100k envelopes,
indexable by sequence). `attach` with `sinceSequence` replays the buffer then streams live. Gap
recovery: client calls `get_state`, re-attaches from its watermark; if the buffer no longer holds
`sinceSequence`, the daemon sends `resync` (full snapshot + truncated replay).

UI bridge round-trips (extension custom UI): the daemon runs native + TS extensions, so
`IRequestExtensionUiAsync` blocks on a pending-UI request; the request is pushed to clients as a
`ui_request` event (own id); one attached client answers with `ui_response { requestId, response }`;
the daemon resolves the pending TCS and the extension resumes. Mirrors RPC mode's
`extension_ui_response`, generalized to a bidirectional lane. Headless (no client attached): UI
request auto-declined (`{ cancelled: true }`) so turns don't hang.

Multi-client policy:
- UI-bridge requests target the most-recently-active client; if unanswered, fall back to the next
  attached client.
- Commands are serialized through `RunExclusiveAsync` (existing Gate); semantic conflicts between
  clients are not arbitrated in v1 (a "controller claim" could be added later).

### Client state model (event sourcing)

New `PiSharp.Client` project:
- `ClientSessionState` is the client's single source of truth: transcript items, busy/idle status,
  model + thinking level, pending tool calls, session metadata, branch info. Only mutated by a pure
  `Apply(event)` reducer and one-time command results (`get_state` on attach, `get_session_snapshot`).
- The reducer consumes flat `AgentSessionEvent` shapes, deserializing payloads with
  `AgentJsonSerializer`. Mirrors today's `TuiRenderState.Reduce`: `message_start/update/end` →
  upsert transcript row by entryId; `tool_execution_*` → upsert tool row by toolCallId;
  `model_select`/`thinking_level_select` → header state; `compaction_*`, `turn_*`, `agent_*` →
  status transitions.
- Rendering unchanged: `TuiRenderState` derived from `ClientSessionState` per frame; `TuiHost` and
  Terminal.GUI views untouched.
- Sequence tracking: client keeps `lastAppliedSequence` per session; attach sends
  `sinceSequence = lastAppliedSequence`. On sequence gap while live: stop applying, `get_state`,
  re-attach from watermark.
- First attach to an existing live session: start at `sinceSequence = head - replayWindow` (daemon
  picks a window, e.g. last 5000 events); older history comes from `get_messages`/JSONL.
- Thread model: single event loop applies socket events to `ClientSessionState`, schedules one
  batched render per frame (reuse `TuiHarnessEventPump` batching approach). No polling.

`RemoteTuiBackend` implements the `TuiHostOptions` surface purely via wire commands + the local
state model: events → state; slash command dispatch; completion; input hooks; theme; session
snapshot/fork; extension shortcuts/registry; UI bridge. `InteractiveMode` swaps its in-process
wiring for this backend; `TuiHost` untouched.

### Daemon lifecycle & CLI UX

- `pisharp` (interactive) → client: read lease → connect → TUI. If no live daemon: spawn
  `pisharp daemon` detached, poll `/health` (10s timeout), then connect.
- `pisharp daemon start` → launch detached daemon (journal/pid tracking so `stop` can find it;
  `--foreground` runs in-process).
- `pisharp daemon stop` / `daemon status` → WS `shutdown` or pid-file signal.
- `--local` flag: force in-process TUI wiring (debugging escape hatch; also used by TUI integration
  tests).
- Lease `~/.pi/PiSharp/daemon.json`: pid, port, api key, started-at, version. Written before the
  host listens; `daemon.lock` exclusive file prevents double-start. Client validates pid alive +
  `/health` 200 + version compat (mismatch → client starts its own daemon on a fresh port).
  API key generated per daemon run (0600 file), sent as WS auth header. `/health` open (no key).
- Kestrel on a chosen free port, bound to `127.0.0.1`.
- Idle timeout: `LiveServerSession` with zero attached clients and no pending turn disposed after
  5 min (configurable); in-progress turns run to completion. `get_state` returns 404 for disposed
  live sessions → client falls back to `create_session` (resume from persisted JSONL).
- Logging: daemon logs to `~/.pi/PiSharp/logs` (existing convention), separate daemon log file.

### Non-interactive modes

`print`, `json`, `subagent-json`, `rpc`, and subagent child processes remain in-process/stdio —
unchanged in behavior.

## Migration phases

1. **Phase 0 — Foundations**: `PiServerHost` library refactor; `daemon` CLI mode scaffolding;
   lease/discovery + health poll + version compat; `--local` flag; client `attach` plumbing.
   No behavior change; tests stay green.

   **Status: implemented** (Tasks 0.4–0.5; the 0.1–0.3 foundations — `PiServerHost` extraction,
   `daemon` CLI scaffolding, lease/discovery — landed in the initial server refactor).
2. **Phase 1 — Server event sourcing**: retained ring-buffer log; sequence-based replay;
   gap/resync; `attach`, `get_state` high-watermark, `resync` commands. `PiSharp.Server.Tests`:
   replay-from-sequence, buffer-overflow → resync, multi-subscriber.

   **Status: implemented** (Tasks 1.1–1.3: `RetainedEventLog`, `attach` + replay-first event pump,
   `get_state` high-watermark + gap recovery).
3. **Phase 2 — Client project + state model**: `PiSharp.Client` with `ClientSessionState`, flat
   reducer, sequence tracking. Unit tests: reducer correctness, gap recovery, attach-from-watermark.

   **Status: implemented** (Tasks 2.1–2.2: `ClientSessionState` + `ClientEventReducer`,
   `ClientSessionConnection` socket protocol client).
4. **Phase 3 — RemoteTuiBackend + InteractiveMode switch**: `RemoteTuiBackend` over wire commands;
   `InteractiveMode` uses it (daemon default, `--local` fallback). Server additions:
   `run_command`/`complete_command`, `process_input`, theme/snapshot/fork-messages/extension-registry
   queries, startup messages/checks, UI-bridge `ui_request`/`ui_response` with multi-client
   responder policy. Integration tests: real client ↔ in-memory/test daemon.

   **Status: implemented** (Tasks 3.1–3.5: TUI runtime facade, server protocol extensions + UI
   bridge, `RemoteTuiBackend`, `InteractiveMode` remote/local switch + auto-start, integration
   tests).
5. **Phase 4 — Lifecycle polish**: idle-timeout disposal, graceful `daemon stop`, log split,
   re-attach UX (`--attach <session>`, last-active recall), multi-terminal verification. Docs:
   README, developer guide, ADR.

   **Status: implemented** (Tasks 4.1–4.4: idle-timeout disposal, graceful `daemon stop`/`status`,
   re-attach plumbing, docs + ADR + README). Note: the `--attach <session>` CLI flag (part of
   Task 4.3) was not added; re-attach works through the live-session/attach protocol instead.
   Task 4.5 (manual E2E verification) is not part of this change.

## Testing strategy

- **Unit**: reducers, protocol serialization, lease/discovery, idle-timeout — per-project suites
  (`PiSharp.Client.Tests` added to `PiSharp.sln`).
- **Integration**: `PiServerHost` in-process on an ephemeral port with a test api key, driven by a
  `ClientWebSocket` test harness (mirrors existing `PiSharp.Server.Tests`). Full TUI flows:
  create → prompt → steer → compact → fork → re-attach.
- **TUI regression**: `PiSharp.Tui.Tests` keep passing (`TuiHost` unchanged; tests run against the
  in-process reducer harness or test doubles of `RemoteTuiBackend`).
- **Manual E2E**: two terminals attach to the same session; close TUI mid-turn and re-open to verify
  transcript catch-up; `daemon stop` mid-turn.

## Open questions / deferred

- Multi-client semantic conflicts (two clients steering simultaneously) — not arbitrated in v1.
- Optional "controller claim" concept for exclusive command rights.
- Replay window size and resync thresholds to be tuned during Phase 1/2 testing.
