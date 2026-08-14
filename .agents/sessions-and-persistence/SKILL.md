---
name: sessions-and-persistence
description: >
  Use when working with session persistence: JSONL session storage
  (JsonlSessionRepo), continuation, forking/branching, compaction, session
  labels, save-points, model/thinking-level changes, session resume, or JS Pi
  JSONL session compatibility. Also covers where session files live and the
  SessionRuntime/session-repo wiring.
type: cross-cutting
scope:
  - src/PiSharp.Agent/Sessions/**
  - src/PiSharp.Compatibility/**
  - src/PiSharp.Runtime/**
  - docs/analysis/pisharp-current-state-catalog.md
related_skills:
  - agent-harness
  - settings-and-resources
  - daemon-protocol
last_verified:
  commit: "646522ccc6edc48acc39e4545cd120af9f1dafba"
  date: "2026-08-14"
confidence: high
---

# Session Persistence and JSONL Compatibility

## When to use this skill

Use this skill when:

- changing session storage format (JSONL);
- changing continuation, forking/branching, or compaction behavior;
- adding a session metadata field (labels, model/thinking changes, save-points);
- debugging session resume;
- working with JS Pi JSONL session compatibility.

Typical tasks include:

- modifying `JsonlSessionRepo`;
- adding a compaction policy;
- changing how sessions fork or branch;
- extending session metadata.

Do not use this skill for:

- the agent loop pipeline — use [agent-harness](../agent-harness/SKILL.md);
- settings/resource paths — use [settings-and-resources](../settings-and-resources/SKILL.md);
- daemon session commands — use [daemon-protocol](../daemon-protocol/SKILL.md).

## Responsibilities and boundaries

This area owns:

- the JSONL session repository;
- session lifecycle (create, continue, fork, compact, dispose);
- session metadata and save-points;
- JS Pi JSONL compatibility.

This area does not own:

- what happens inside a turn (agent-harness);
- how sessions are addressed over the wire (daemon-protocol);
- settings precedence (settings-and-resources).

## Architecture

Sessions are JSONL-backed records managed by `JsonlSessionRepo` (an
`ISessionRepo<JsonlSessionMetadata, JsonlSessionCreateOptions,
JsonlSessionListOptions>`). `SessionRuntime` holds the repo and creation
options, and builds harnesses via a factory. Features include continuation,
forking/branching, compaction, labels, model/thinking-level changes, and
save-points. Default session files live under `~/.pi/agent/sessions`, matching
JS Pi layout for compatibility.

The JSONL format itself must stay compatible with the original JS Pi session
format (header version, leaf entries); the compatibility layer
(`src/PiSharp.Compatibility`) guards these conventions.

### Important components

| Component | Location | Responsibility |
|---|---|---|
| Session repo | `src/PiSharp.Agent/Sessions` (`JsonlSessionRepo`) | JSONL persistence |
| Session runtime | `src/PiSharp.Runtime/SessionRuntime.cs` | Holds repo + create options + harness factory |
| Compatibility | `src/PiSharp.Compatibility` | JS Pi session/resource conventions |
| Session commands | `src/PiSharp.Server` (wire) | `create_session`, `fork`, `compact`, `switch_session`, etc. |

### Main flow

1. Runtime composes the session repo with create options.
2. A session is created/continued (local or via daemon wire commands).
3. The harness appends turns to the session through the repo.
4. Compaction/fork/label operations rewrite session state (JSONL-safe).

## Project terminology

| Term | Meaning in this repository |
|---|---|
| JSONL | Line-delimited JSON session format |
| JsonlSessionRepo | The repository implementation |
| SessionMetadata | Per-session record (labels, model, thinking level, save-points) |
| Compaction | Rewriting session to a summarized/trimmed form |
| Fork/branch | Diverging a session into a new continuation |
| Leaf entries | Terminal entries in the JSONL session tree (JS Pi compat) |

## Important entry points
- [`skills/SKILL.md`](../../SKILL.md): project router — routing index for all PiSharp project skills.


- [`src/PiSharp.Agent/Sessions`](../../../src/PiSharp.Agent/Sessions)
- [`src/PiSharp.Runtime/SessionRuntime.cs`](../../../src/PiSharp.Runtime/Runtime/SessionRuntime.cs)
- [`src/PiSharp.Compatibility`](../../../src/PiSharp.Compatibility)
- [`docs/analysis/current-state-catalog.md`](../../../docs/analysis/pisharp-current-state-catalog.md)
  (§2.6 documents JSONL internals)

## Dependencies and consumers

### Depends on

- `src/PiSharp.Agent` (session model), `src/PiSharp.Compatibility` (format
  conventions).

### Consumed by

- `AgentHarness` (per-turn persistence), the daemon (session commands), the
  CLI/TUI (session list/switch/fork UI).

### External systems

- The filesystem (session files under the Pi home).

## Invariants

The following must remain true:

1. Session JSONL stays compatible with the JS Pi session format — header
   version and leaf entry conventions must not break continuation of existing
   sessions.
2. Session files default under `~/.pi/agent/sessions`.
3. Every session write goes through the repo — no direct file manipulation by
   callers.
4. Compaction must not lose the ability to resume the session.
5. Fork/branch operations produce sessions that continue cleanly.

## Common change workflows

### Change session metadata

1. Extend `JsonlSessionMetadata` and the create/update options.
2. Update the repo read/write path.
3. If the wire protocol exposes the field, update `ServerContracts.cs` and
   clients (see [daemon-protocol](../daemon-protocol/SKILL.md)).

Files commonly changed together:

- `src/PiSharp.Agent/Sessions/**`
- `src/PiSharp.Server/Contracts/ServerContracts.cs`

Validation:

```bash
dotnet build PiSharp.sln
dotnet test tests/PiSharp.Agent.Tests/PiSharp.Agent.Tests.csproj
```

### Change compaction behavior

1. Locate the compaction service under `src/PiSharp.Agent/Sessions`.
2. Change the policy; verify resume still works after compaction.
3. Add/adjust tests covering compaction-then-resume.

Files commonly changed together:

- `src/PiSharp.Agent/Sessions/**`
- `tests/PiSharp.Agent.Tests/**`

Validation:

```bash
dotnet test tests/PiSharp.Agent.Tests/PiSharp.Agent.Tests.csproj
dotnet test PiSharp.sln
```

## Testing and validation

Run for all changes in this area:

```bash
dotnet build PiSharp.sln
dotnet test tests/PiSharp.Agent.Tests/PiSharp.Agent.Tests.csproj
```

Run conditionally:

```bash
dotnet test PiSharp.sln
```

## Operational considerations

- Session files are user data — format changes must migrate or remain
  backward-compatible; never silently drop user sessions.
- The JSONL internals are documented only in
  `docs/analysis/current-state-catalog.md` §2.6; treat code as the source of
  truth when the doc and code disagree.

## Common mistakes

- Do not change the JSONL header/leaf format without preserving JS Pi
  compatibility — existing sessions and JS Pi interop depend on it.
- Do not bypass the repo with direct file writes.
- Do not log session contents (they may contain sensitive conversation data).
- Do not assume `docs/analysis/current-state-catalog.md` is fully current.

## Legacy and deprecated patterns

- The original JS Pi session format is the compatibility target; the
  compatibility layer must keep reading/writing it. This is intentional legacy
  support, not debt to remove.

## Existing authoritative documentation

- [`docs/analysis/current-state-catalog.md`](../../../docs/analysis/pisharp-current-state-catalog.md)

  * Covers JSONL session internals (§2.6) not documented in current guides.
  * Treat as a baseline, but verify against code — the doc is stale elsewhere
    (e.g. daemon section).

## Known ambiguity and technical debt

- Session JSONL internals are under-documented in the current developer guide;
  the analysis catalog is the only reference and it lags in places.
- Compaction + fork interactions are subtle; test resume paths explicitly.

## Evidence and verification

This skill was verified against commit `646522ccc6edc48acc39e4545cd120af9f1dafba`.

Primary evidence:

- [`src/PiSharp.Agent/Sessions`](../../../src/PiSharp.Agent/Sessions)
- [`src/PiSharp.Runtime/SessionRuntime.cs`](../../../src/PiSharp.Runtime/Runtime/SessionRuntime.cs)
- [`src/PiSharp.Compatibility`](../../../src/PiSharp.Compatibility)
- [`docs/analysis/current-state-catalog.md`](../../../docs/analysis/pisharp-current-state-catalog.md)
