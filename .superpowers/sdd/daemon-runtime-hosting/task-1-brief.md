# Task 1 — Server: session-scoped UI bridge + configurable interactive timeout

Working directory: `G:/code/AI/pi/PiSharp/.worktrees/daemon-runtime-hosting`
Plan reference: `docs/superpowers/plans/2026-08-15-daemon-runtime-hosting.md` (Task 1)

## Requirements (authoritative)

Add a **session-scoped** overload to `PiServerUiBridge` so a command running on a specific `LiveServerSession` can drive `select`/`input` UI on THAT session (the current bridge emits only to the most-recently-created session globally), with an explicit per-call response timeout.

**RESOLUTION (overrides the plan's "PiServerHostOptions.InteractiveUiResponseTimeout" text):** The overload takes an explicit `TimeSpan? responseTimeout` per call. Do NOT add a `PiServerHostOptions` host option, and do NOT alter `ServerUiBridge`'s constructor/DI registration.

### Files
- Modify: `src/PiSharp.Server/UiBridge/IServerUiBridge.cs`
- Modify: `src/PiSharp.Server/UiBridge/ServerUiBridge.cs`
- Test: `tests/PiSharp.Server.Tests/PiServerUiBridgeTests.cs` (new)

### Exact changes
1. `IServerUiBridge` gains:
   `Task<ServerUiResponse> RequestUiAsync(ServerUiIntent intent, LiveServerSession target, TimeSpan? responseTimeout, CancellationToken ct = default)`
   Keep the existing `Task<ServerUiResponse> RequestUiAsync(ServerUiIntent intent, CancellationToken ct = default)` unchanged (extension lane).
2. `ServerUiBridge`:
   - Extract `RequestUiAsync` body into `RequestUiAsyncCore(ServerUiIntent intent, LiveServerSession? target, TimeSpan? responseTimeout, CancellationToken ct)`.
   - `EmitUiRequest(intent, target)` emits on `target ?? SelectSession()` — thread the target through instead of always `SelectSession()`.
   - The internal auto-cancel timeout becomes `responseTimeout ?? ResponseTimeout`.
   - Public overloads: `RequestUiAsync(intent, ct)` → core(..., null, null, ct); `RequestUiAsync(intent, target, responseTimeout, ct)` → core(..., target, responseTimeout, ct).
   - Do not change the existing theme interception behavior.
3. New test `tests/PiSharp.Server.Tests/PiServerUiBridgeTests.cs`:
   - Mirror the raw-WebSocket client harness from `tests/PiSharp.Server.Tests/HostIntegrationTests.cs` (`StartHostAsync` + `RawClient`).
   - Test A: a `select`-kind request made through the **target-scoped** overload resolves to the answered value when the client sends `ui_response`.
   - Test B: a generous `responseTimeout` (e.g. `TimeSpan.FromSeconds(30)`) does NOT auto-cancel before the client answers (the fixed 5s would).
   - Follow existing xUnit conventions in the project (the Server tests use `[Fact]`).

## Global constraints that bind this task
- Do not touch `javascript/`. Work only in `src/` and `tests/`.
- `PiSharp.Server` cannot reference `PiSharp.Cli` or `PiSharp.Tui`.
- Never log API keys or session ids.
- TDD: write the failing test first; confirm it fails; then implement; confirm it passes. Re-run the full `PiSharp.Server.Tests` project serially (`-m:1`) before committing.
- The bridge response types: `ServerUiIntent`/`ServerUiResponse` in `src/PiSharp.Server/Contracts/ServerContracts.cs:253-262`; `LiveServerSession` in `src/PiSharp.Server/Runtime/LiveServerSession.cs` (exposes `EmitEvent(...)`).

## Deliverable / report contract
Write your full report to `.superpowers/sdd/daemon-runtime-hosting/task-1-report.md` (in the worktree). Return only: status (DONE / DONE_WITH_CONCERNS / NEEDS_CONTEXT / BLOCKED), commits (short hashes + messages), a one-line test summary (counts + the command run), and any concerns.

Commit message convention: `feat(server): session-scoped UI bridge and configurable interactive timeout`.
