# ADR: Daemon + Event-Sourced Client Architecture

Date: 2026-08-14

Status: Accepted

## Context

Today `pisharp` is a monolith: every invocation builds a `SessionRuntime` in-process and wires the
TUI directly into the runtime. Every run pays full startup cost (extension loading, TypeScript
bridge, resource discovery, session scan); long turns block the UI; and only one terminal can talk
to a given runtime at a time. A standalone `PiSharp.Server` (ASP.NET Core WebSocket host) already
existed with API-key auth, a `ServerSessionRegistry`, a session command set, and per-session
sequence-numbered event streaming — but nothing connected to it.

We wanted to eliminate the per-run startup cost, keep the TUI responsive during turns, support
multiple live sessions and multiple terminals attaching to the same live session, and preserve
session state across client restarts (crash resilience).

## Decision

Split the app into a per-user **daemon** (backend) and a **TUI client** (frontend) that communicate
over a localhost WebSocket using an **event-sourced** protocol: the client reconstructs all UI
state by replaying and applying a sequence-numbered event stream.

- **Daemon**: `pisharp daemon` hosts the server wiring. `PiServerHost` (Kestrel) listens on
  `127.0.0.1:<port>` with a `/health` endpoint (open, no key) and a `/ws` WebSocket endpoint
  (API-key protected). One daemon per user; lease at `~/.pi/PiSharp/daemon.json` (pid, port, api
  key, started-at, version); exclusive `daemon.lock` prevents double-start. Live sessions run warm
  in the daemon and survive client exit; a detached session with zero attached clients and no
  pending work is disposed after an idle timeout (default 5 minutes).
- **Client**: `pisharp` in interactive mode reads the lease, health-checks the daemon (pid alive +
  `/health` 200 + runtime version major/minor match), and auto-starts one when absent. It connects
  over WebSocket (`Authorization: Bearer <key>`) and drives the TUI through `RemoteTuiBackend`, a
  wire implementation of the TUI host surface. `--local` forces the in-process TUI wiring as a
  debugging/testing fallback; when the daemon cannot be started, the client falls back to
  in-process mode with a warning.
- **Event-sourced client state**: `ClientSessionState` is the client's single source of truth and
  is mutated only by a pure reducer (`ClientEventReducer`) folding flat `AgentSessionEvent`s, plus
  one-time command results on attach. The client tracks `lastAppliedSequence` per session.
- **Key contracts**:
  - `ServerCommandEnvelope` — flat request frame (`type`, `id`, `serverSessionId` plus payload
    fields merged into one JSON object).
  - `ServerResponse` — correlated reply (`Id` matches the command envelope's `Id`; `Success`,
    `Data`, `Error`).
  - `ServerEventEnvelope` — `{ Type: "event", ServerSessionId, Sequence (monotonic, per session),
    Timestamp, Event: AgentSessionEvent }`.
  - **Retained event log + replay**: each `LiveServerSession` keeps an in-memory ring buffer of the
    most recent 100,000 envelopes, indexable by sequence.
  - **`attach { sessionId, sinceSequence }`**: replays the retained log from `sinceSequence`, then
    streams live; the response reports `FromSequence`, `HeadSequence`, `Gap`, `ReplayedCount`.
  - **Gap recovery**: on a detected sequence gap (including a replay whose `sinceSequence` predates
    the retained window), the client calls `get_state` (which carries the session `HighWatermark`),
    folds the snapshot, and re-attaches from its watermark; the daemon reports `Gap` when the buffer
    no longer holds the requested sequence.
  - Commands: `create_session`, `attach`, `prompt`, `steer`, `follow_up`, `queue_next_turn`,
    `abort`, `fork`, `run_command`, `complete_command`, `process_input`, `shutdown`, plus
    `get_state`/`get_messages`/`list_sessions`/`set_model`/`set_thinking_level`/`compact`/
    `new_session`/`switch_session`/`set_session_name` and the query/UI-bridge set
    (`get_theme`, `get_session_snapshot`, `get_fork_messages`, extension queries, `ui_response`,
    `get_startup_messages`, `post_startup_checks`, `cycle_thinking_level`, `get_available_models`,
    `get_last_assistant_text`). Async work (`prompt`, `compact`, `run_command`) returns
    `{ accepted = true }` immediately; lifecycle flows over the event stream.
  - **UI bridge**: extension UI requests are pushed to attached clients as `ui_request` events and
    answered with `ui_response { requestId, value, cancelled }`; unanswered requests auto-cancel
    after a short timeout so turns never hang.

## Consequences

Positive:

- Fast TUI startup: runtimes stay warm in the daemon; the client only pays WebSocket connect cost.
- Multi-client attach: several terminals can attach to the same live session, each with its own
  replay cursor.
- Crash resilience: the client rebuilds all UI state by replaying the retained event log, so a
  restarted client catches up without the daemon re-executing work.
- `daemon stop` shuts down gracefully (responds first, then stops the host); `daemon status`
  reports port/pid/liveness from the lease.

Negative:

- Version skew management: the client validates the daemon's runtime major/minor version against
  its own and starts a fresh daemon on a new port on mismatch; protocol evolution must stay
  backward compatible or bump the compatibility check.
- A wire protocol surface to maintain: every new command needs a `ServerCommandTypes` const, a
  command record in `ServerContracts.cs`, and a `case` in `PiServerWebSocketHandler`.
- WebSocket bridge complexity: command correlation, event pumping, gap detection/resync, and the
  UI-bridge round-trip lane are new moving parts with their own failure modes.
- One daemon per user on a fixed localhost port means a stale lease (dead pid, port collision) must
  be detected and recovered at connect time.
