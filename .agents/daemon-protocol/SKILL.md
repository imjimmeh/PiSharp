---
name: daemon-protocol
description: >
  Use when changing the daemon/client wire protocol: server commands and events
  (ServerCommandTypes, ServerCommandEnvelope, ServerContracts.cs), the
  WebSocket hub (PiServerWebSocketHandler), event-sourced client state
  (ClientEventReducer), replay/attach (sinceSequence, get_state, gap recovery),
  the SDK surface, the daemon lease, or adding a daemon wire command.
type: cross-cutting
scope:
  - src/PiSharp.Server/**
  - src/PiSharp.Client/**
  - src/PiSharp.Cli/Modes/DaemonMode.cs
  - src/PiSharp.Sdk/**
  - docs/adr/2026-08-14-daemon-client-architecture.md
related_skills:
  - tui-development
  - plugin-portfolio
  - repository-overview
last_verified:
  commit: "646522ccc6edc48acc39e4545cd120af9f1dafba"
  date: "2026-08-14"
confidence: high
---

# Daemon and Event-Sourced Wire Protocol

## When to use this skill

Use this skill when:

- adding or changing a daemon wire command;
- changing the event envelope or a `ClientEventReducer` case;
- altering replay/attach/gap-recovery behavior;
- debugging daemon/client behavior;
- changing the SDK surface;
- changing daemon authentication or the lease file.

Typical tasks include:

- adding a `ServerCommandTypes` constant + contract record + handler case;
- changing `HighWatermark` semantics;
- altering `sinceSequence` attach behavior;
- updating `pisharp daemon foreground` startup.

Do not use this skill for:

- TUI rendering of daemon state — use [tui-development](../tui-development/SKILL.md);
- plugin internals that ride the protocol — use [plugin-portfolio](../plugin-portfolio/SKILL.md);
- build/run mechanics — use [local-development](../local-development/SKILL.md).

## Responsibilities and boundaries

This area owns:

- the wire command/event contract;
- the event-sourced client reducer;
- replay/attach/gap recovery;
- the daemon lease and API-key auth;
- the SDK's protocol surface.

This area does not own:

- the agent loop itself (agent-harness);
- plugin implementations (plugin-portfolio);
- TUI rendering (tui-development).

## Architecture

The daemon (`PiSharp.Server`) is a Kestrel host exposing `/health` and `/ws`
with API-key auth. It hosts warm `SessionRuntime`s. Clients (`PiSharp.Cli`,
`PiSharp.Client`, `PiSharp.Sdk`, the webapp) are event-sourced WebSocket
clients: they reduce server events into client state and can attach with a
`sinceSequence`, recovering gaps via `get_state`. The server retains a ring
buffer of the last ~100k envelopes.

Commands are flat `ServerCommandEnvelope(string Type, string? Id, string?
ServerSessionId)` records; async work returns "accepted" and results flow back
as events. `ServerMessagesResult` carries a `HighWatermark` for replay
coordination.

### Important components

| Component | Location | Responsibility |
|---|---|---|
| Command types | `src/PiSharp.Server/Contracts/ServerContracts.cs` | `ServerCommandTypes` constants + command/result records |
| WebSocket hub | `src/PiSharp.Server/WebSockets/PiServerWebSocketHandler.cs` | Dispatch: one case per command type |
| Server host | `src/PiSharp.Server` (PiServerHost) | Kestrel `/health` + `/ws`, API-key auth, warm runtimes |
| Client reducer | `src/PiSharp.Client/ClientEventReducer.cs` | Pure event -> state reduction |
| Daemon mode | `src/PiSharp.Cli/Modes/DaemonMode.cs` | `pisharp daemon foreground` entry |
| SDK | `src/PiSharp.Sdk` | Programmatic client surface |

### Main flow

1. `pisharp daemon foreground` starts `PiServerHost` (Kestrel, `/health` + `/ws`).
2. Clients connect over WebSocket with API-key auth and issue flat commands.
3. The handler dispatches each command type; async commands return "accepted"
   and emit events later.
4. Clients reduce events (pure `ClientEventReducer`) into local state.
5. Attach: client requests `sinceSequence`; the server replays from the retained
   ring buffer; gaps are recovered via `get_state`.

## Project terminology

| Term | Meaning in this repository |
|---|---|
| Daemon | Long-lived `PiSharp.Server` process |
| Envelope | `ServerCommandEnvelope` / event envelope on the wire |
| Sequence | Monotonic event ordering for replay |
| HighWatermark | Last-applied sequence used for replay coordination |
| Lease | `~/.pi/PiSharp/daemon.json` — daemon ownership/address record |
| get_state | Command returning authoritative server state for gap recovery |

## Important entry points
- [`skills/SKILL.md`](../../SKILL.md): project router — routing index for all PiSharp project skills.


- [`docs/adr/2026-08-14-daemon-client-architecture.md`](../../../docs/adr/2026-08-14-daemon-client-architecture.md):
  canonical architecture decision.
- [`src/PiSharp.Server/Contracts/ServerContracts.cs`](../../../src/PiSharp.Server/Contracts/ServerContracts.cs)
- [`src/PiSharp.Server/WebSockets/PiServerWebSocketHandler.cs`](../../../src/PiSharp.Server/WebSockets/PiServerWebSocketHandler.cs)
- [`src/PiSharp.Client/ClientEventReducer.cs`](../../../src/PiSharp.Client/ClientEventReducer.cs)

## Dependencies and consumers

### Depends on

- `src/PiSharp.Runtime` (warm `SessionRuntime`s), `src/PiSharp.Agent`
  (session repo, harness).

### Consumed by

- `src/PiSharp.Cli` (client mode), `src/PiSharp.Tui` (remote backend),
  `src/PiSharp.Sdk`, `src/pisharp-session-webapp`.

### External systems

- None directly; the wire protocol is internal.

## Invariants

The following must remain true:

1. Commands are flat envelopes: `ServerCommandEnvelope(Type, Id?, ServerSessionId?)`.
2. Async work returns "accepted" immediately; results flow as events — never
   block the socket on long work.
3. `ClientEventReducer` stays a pure function of (state, event) — no I/O, no
   side effects in reduction.
4. Attach must be possible with `sinceSequence` and recover gaps via `get_state`.
5. Every wire command has all three touchpoints together: `ServerCommandTypes`
   const, contract record in `ServerContracts.cs`, dispatch case in
   `PiServerWebSocketHandler`.
6. The daemon authenticates `/ws` with an API key; the lease file
   (`~/.pi/PiSharp/daemon.json`) records daemon ownership.

## Common change workflows

### Add a daemon wire command

Use this process when adding a new server command.

1. Add a const to `ServerCommandTypes`.
2. Add the command record (and result record if any) in `ServerContracts.cs`.
3. Add the dispatch case in `PiServerWebSocketHandler` (handle `Type`, `Id`,
   `ServerSessionId`, and the "accepted then event" pattern for async work).
4. Add client-side handling in `ClientEventReducer` and/or the SDK if the
   command has a client-facing result.

Files commonly changed together:

- `src/PiSharp.Server/Contracts/ServerContracts.cs`
- `src/PiSharp.Server/WebSockets/PiServerWebSocketHandler.cs`
- `src/PiSharp.Client/ClientEventReducer.cs`
- `src/PiSharp.Sdk/**` (if SDK-exposed)

Validation:

```bash
dotnet build PiSharp.sln
dotnet test tests/PiSharp.Server.Tests/PiSharp.Server.Tests.csproj
dotnet test tests/PiSharp.Client.Tests/PiSharp.Client.Tests.csproj
```

### Change replay/attach behavior

1. Change the retained ring buffer or `sinceSequence` handling server-side.
2. Update `ClientEventReducer`/attach flow client-side.
3. Keep `HighWatermark` semantics consistent on both sides.

Files commonly changed together:

- `src/PiSharp.Server/WebSockets/**`
- `src/PiSharp.Client/ClientEventReducer.cs`

Validation:

```bash
dotnet test tests/PiSharp.Client.Tests/PiSharp.Client.Tests.csproj
dotnet test PiSharp.sln
```

## Testing and validation

Run for all changes in this area:

```bash
dotnet build PiSharp.sln
```

Run conditionally:

```bash
dotnet test tests/PiSharp.Server.Tests/PiSharp.Server.Tests.csproj
dotnet test tests/PiSharp.Client.Tests/PiSharp.Client.Tests.csproj
dotnet test PiSharp.sln
```

## Operational considerations

- The daemon lease (`~/.pi/PiSharp/daemon.json`) is user-specific; never assume
  its exact contents in code.
- API-key auth: never log the key; document mechanism, not values.
- The retained ring buffer (~100k envelopes) bounds replay depth; changing it
  changes gap-recovery behavior.

## Common mistakes

- Do not add a command const without its `ServerContracts.cs` record and
  handler case — the three touchpoints ship together.
- Do not perform blocking work in the WebSocket handler; use accepted-then-event.
- Do not add I/O or side effects to `ClientEventReducer` — it must stay pure.
- Do not change `HighWatermark` semantics on one side only.

## Legacy and deprecated patterns

- The current daemon/client design supersedes earlier in-process-only
  architectures; `docs/analysis/current-state-catalog.md` §11 still claims the
  daemon is "not in main" — that is stale.

## Existing authoritative documentation

- [`docs/adr/2026-08-14-daemon-client-architecture.md`](../../../docs/adr/2026-08-14-daemon-client-architecture.md)

  * Covers the daemon/client split, event sourcing, replay, auth, and the
    three-touchpoint command workflow.
  * Treat as authoritative for the protocol.
  * Does not enumerate every current command — read `ServerContracts.cs`.

## Known ambiguity and technical debt

- Command/event surface grows with plugins (plan-mode, themes, packages,
  skills, metrics); the ADR describes the pattern, not the full list.
- Ring-buffer retention size is a tuning constant; its exact value should be
  confirmed in code before relying on replay depth.

## Evidence and verification

This skill was verified against commit `646522ccc6edc48acc39e4545cd120af9f1dafba`.

Primary evidence:

- [`docs/adr/2026-08-14-daemon-client-architecture.md`](../../../docs/adr/2026-08-14-daemon-client-architecture.md)
- [`src/PiSharp.Server/Contracts/ServerContracts.cs`](../../../src/PiSharp.Server/Contracts/ServerContracts.cs)
- [`src/PiSharp.Server/WebSockets/PiServerWebSocketHandler.cs`](../../../src/PiSharp.Server/WebSockets/PiServerWebSocketHandler.cs)
- [`src/PiSharp.Client/ClientEventReducer.cs`](../../../src/PiSharp.Client/ClientEventReducer.cs)
