# Implementation Plan — Fix Adversarial Review Findings

**Date:** 2026-08-15
**Author:** dispatched worker (task_f4af32d78d3b)
**Source:** `docs/adversarial-review.md` (2026-08-15); all paths below re-verified against the working tree before writing this plan.
**Scope:** PLANNING ONLY. No code was modified while producing this plan.

---

## Goal

Fix, in the ranked order of review §6, the top findings of the adversarial review: (1) harness queue races, (2) child-process kill/pipe handling, (3) middleware chain composition, (4) WebSocket server hardening, (5) file memory backend index/flush, (6) fail-closed permissions + gated extension spawns, (7) atomic OAuth storage, (8) corrupt-JSONL tolerance, (9) grant-key encoding. Item 10 (de-god `SessionRuntime`) is deliberately deferred (weeks of work, highest risk); one cheap correctness item from it is folded into Phase J.

Deliverable of each phase: production change + failing-first tests + green build, per repo TDD convention (`tests/` tree, xUnit `[Fact]`).

---

## Current context & assumptions

- Solution builds with `dotnet build PiSharp.sln`; test projects live under `tests/` (one per area). Relevant: `PiSharp.Agent.Tests`, `PiSharp.Runtime.Tests`, `PiSharp.Memory.Tests`, `PiSharp.Permissions.Tests`, `PiSharp.Server.Tests`, `PiSharp.Mcp.Tests`, `PiSharp.Coordination.Tests`, `PiSharp.Ai.Tests`.
- Test conventions observed in `tests/PiSharp.Agent.Tests/Harness/AgentHarnessTests.cs`, `AgentHarnessEventTests.cs`, `AgentHarnessPipelineTests.cs`, `LoopEventStageTests.cs`: xUnit `[Fact]`, `AgentHarness<JsonlSessionMetadata>` built via `MemorySessionStorage<JsonlSessionMetadata>` + `FakeStream("ok")`, `Subscribe` to observe events, `Assert.Collection/Contains/Single`.
- Harness tests exist today for `Steer` behavior (`AgentHarnessEventTests.cs:170-178` asserts `QueueUpdate` events) — new concurrency tests extend this file or add `Harness/AgentHarnessConcurrencyTests.cs`.
- The three top findings (2.1, 2.2/2.3, 1.3) are confirmed in source and were independently spot-checked by the review; this plan re-confirmed them (see Appendix).
- Assumption: no behavior contracts beyond those in `docs/pisharp-developer-guide.md` / `docs/pisharp-runtime.md` constrain queue orderings; queue drain ordering is preserved (FIFO) in every option below.

---

## Approach

Work phase-by-phase in review §6 order (highest risk first). Each phase is TDD:
1. **Red** — write the failing test(s) pinning the bug (race, leak, wrong decision, corrupt-input crash, mis-encoded key).
2. **Green** — minimal production change.
3. **Refactor** — tighten, dedupe, run phase tests + affected-project tests.
4. Phase gate: `dotnet build PiSharp.sln` must stay green; per-phase `dotnet test <project> --filter <pattern>` green.

Bite-sized tasks below are each 2–5 minutes of focused work; a task is "done" when its validation line passes. Global validation runs once at the end (see Tests & validation).

---

## Phase A — Harness queue races (review §6 #1 / finding 2.1) — *#1 latent crash source*

**Objective:** make all harness queues/listeners safe under concurrent `Add`/`Clear`/`ToArray` from arbitrary threads (WS handler `Task.Run` path) while preserving FIFO drain and `QueueUpdate` semantics.

### Task A1 — Red: concurrency stress test
- **Files:** `tests/PiSharp.Agent.Tests/Harness/AgentHarnessConcurrencyTests.cs` (new).
- **Change:** test spins N=8 producer tasks × 500 `harness.Steer(msg)` + `FollowUp` + `QueueNextTurn` from `Task.Run`, while the loop body drains `DrainSteeringQueueAsync`/`DrainFollowUpQueueAsync`/`DrainNextTurnQueueAsync` in a loop until all producers finish; then asserts every message was drained exactly once and no exception escaped (also exercise `_pendingWrites` by running a `PromptAsync` concurrently with `FlushWritesAsync` via the pipeline). Run pre-fix: expect intermittent `ArgumentOutOfRangeException`/lost messages (may need a few iterations).
- **Validate:** `dotnet test tests/PiSharp.Agent.Tests/PiSharp.Agent.Tests.csproj --filter "FullyQualifiedName~AgentHarnessConcurrency"` fails (red).
- **Outcome:** a reproducible test that fails on the current unlocked `List<>` implementation.

### Task A2 — Green: lock or channel the steer queues
- **Files:** `src/PiSharp.Agent/Harness/AgentHarness.cs:29-33` (field decls), `:91/:98/:105` (`Steer`/`FollowUp`/`QueueNextTurn` adds), `:695-714` (`DrainQueueAsync`), `:688-693` (`EmitQueueUpdate`).
- **Change:** Option **lock** (boring, minimal diff): add `private readonly object _queueGate = new();` guard all three queue `Add`/`ToArray`/`Clear` sites with `lock (_queueGate)`; `DrainQueueAsync` does `lock { copy = queue.ToArray(); queue.Clear(); }` then emits update outside the lock. Keep FIFO.
- **Validate:** concurrency test green; existing `AgentHarnessEventTests` `Steer`/`QueueUpdate` test still green.
- **Outcome:** no concurrent `List` mutation; drain is atomic snapshot.

### Task A3 — Green: `_pendingWrites` + `_listeners` safety
- **Files:** `src/PiSharp.Agent/Harness/AgentHarness.cs:33` (`_listeners`), `:84-85` (`Subscribe`/unsubscribe), `:565` (`QueueWriteOrAppendAsync` add), `:622-629` (`FlushWritesAsync`), `:585` (`CreateEventContext` listener snapshot).
- **Change:** same lock (`_listenersGate` + reuse `_queueGate` or one combined gate — keep one lock per concern, no nesting): guard `_listeners.Add/Remove`, `_listeners.ToArray()` snapshot; guard `_pendingWrites.Add/ToArray/RemoveRange`.
- **Validate:** concurrency test (extended with subscribe/unsubscribe mid-drain) green.
- **Outcome:** listener set and pending writes consistent under concurrent mutation.

### Task A4 — Green: mirror fix on the Agent loop state (consistency with 2.1's "AgentHarness.cs, Agent.cs")
- **Files:** `src/PiSharp.Agent/AgentState.cs:53-63` (`PendingMessageQueue`: plain `Queue<AgentMessage>` with unguarded `Enqueue`/`Drain`/`HasItems`), `src/PiSharp.Agent/Agent.cs:16,41-42,191` (`_listeners` plain `List<>` with unguarded `Add`/`Remove`; notify snapshots via `_listeners.ToArray()`).
- **Change:** apply the same lock discipline as A2/A3: guard `PendingMessageQueue` with its own lock (or swap to `ConcurrentQueue<T>` + `Interlocked` flags), and guard `Agent._listeners` `Add`/`Remove`/`ToArray` with a lock. `Agent.cs` already uses `_notificationGate`/`_flushGate` for other state — reuse the pattern, do not nest.
- **Validate:** extend the A1 concurrency test to also drive `Agent.Steer`/`FollowUp` while `AgentLoop` drains (`GetSteeringMessages`/`GetFollowUpMessages` at `Agent.cs:121-122`); existing `PiSharp.Agent.Tests` loop tests green.
- **Outcome:** both queue implementations in the steer path are race-free — no half-fixed pattern left behind.
 
### Task A5 — Consider channel alternative (decision recorded, not both)

- **Files:** none (decision).
- **Change:** If the lock diff proves noisy in `CreateEventContext`/pipeline callers, switch the three steer queues to `Channel<AgentMessage>` (bounded, single reader) and drain with `ReadAllAsync`; `EmitQueueUpdate` snapshots via the channel's visible contents. Decide in review; do NOT ship both.
- **Validate:** same tests + existing pipeline tests (`AgentHarnessPipelineTests.cs`).
- **Outcome:** one mechanism, documented in the PR.

**Phase A gate:** `dotnet test tests/PiSharp.Agent.Tests/PiSharp.Agent.Tests.csproj` green; `dotnet build PiSharp.sln` green.

---

## Phase B — Kill children on timeout/cancel, bound output, non-blocking pipe read (review §6 #2 / findings 2.2+2.3)

**Objective:** a timed-out or cancelled `ExecAsync` never orphans a child process tree; output capture is bounded; the stdout/stderr reader never blocks on sync disk I/O.

### Task B1 — Red: orphan and hang tests
- **Files:** `tests/PiSharp.Runtime.Tests/SystemExecutionEnvTests.cs` (new; extend existing `PiSharp.Runtime.Tests` if a partial exists).
- **Change:** (a) run a long-lived child (Windows: `powershell -Command Start-Sleep 60`; else `sleep 60`), cancel the `CancellationTokenSource` after ~500 ms, assert result is `Aborted`/`Timeout` AND the spawned pid is no longer alive (probe `Process.GetProcessById`; on Windows also assert no child survived via `tasklist /FI "PID eq <pid>"`); (b) chatty child (`powershell -Command "1..100000 | ForEach-Object { Write-Host $_ }"` style) with a slow `OnOutputBytes` callback (e.g. 5 ms delay) must complete within a bound instead of stalling forever on the pipe.
- **Validate:** both fail pre-fix (process still alive; test (b) hangs → keep timeout guard ~10 s so it fails fast).
- **Outcome:** failing tests pinning orphan + pipe-stall bugs.

### Task B2 — Green: kill on cancellation/timeout
- **Files:** `src/PiSharp.Runtime/IO/SystemExecutionEnv.cs:159-203` (`ExecAsync`).
- **Change:** in the `catch (OperationCanceledException …)` blocks (191-198) and a `finally` when the process was started, call `process.Kill(entireProcessTree: true)` guarded by `try/catch` (process may already have exited) before returning the error result; `process.Dispose` already runs via `using`. Note: `WaitForExitAsync(linkedCts.Token)` throws before exit; after kill, call `process.WaitForExit()` (no token) briefly to reap.
- **Validate:** orphan test green on Windows and Unix.
- **Outcome:** no orphaned children on timeout/cancel.

### Task B3 — Green: async pipe readers (remove sync-over-async)
- **Files:** `src/PiSharp.Runtime/IO/SystemExecutionEnv.cs:182-188, 209-219`.
- **Change:** drop `OutputDataReceived`/`ErrorDataReceived` + `BeginOutputReadLine`/`BeginErrorReadLine`; instead start two `Task`s reading `await process.StandardOutput.ReadLineAsync(linkedCts.Token)` / `StandardError.ReadLineAsync` into `HandleLineAsync` (async version). `HandleLineAsync` awaits `bytesCallback(...)` instead of `.GetAwaiter().GetResult()` (line 217). `HandleLine` (209-219) becomes `HandleLineAsync` (or an async local). Await both readers + `WaitForExitAsync` together; cancel readers when the process exits.
- **Validate:** chatty-child test (b) completes; existing CLI/session tests that shell out (`PiSharp.Cli.Tests`, `PiSharp.Acp.Tests` use `SystemExecutionEnv`) stay green.
- **Outcome:** pipe readers never block the underlying stream; no sync-over-async in the hot read path.

### Task B4 — Green: bound capture magnitudes
- **Files:** `src/PiSharp.Runtime/IO/SystemExecutionEnv.cs:180-181, 212`.
- **Change:** cap `stdout`/`stderr` capture (e.g. 1 MiB or 50k lines each); on overflow append a single `…[truncated]` marker and stop appending (callbacks still flow until consumer bound). Make caps `const` (replace magic numbers) or `ExecutionOptions` fields.
- **Validate:** a child emitting 10 MB of output returns bounded `ShellResult` strings with truncation marker; `OnOutputBytes` still receives all lines (streaming unchanged).
- **Outcome:** bounded memory per exec; contract of `OnStdout/OnStderr/OnOutputBytes` preserved.

**Phase B gate:** `dotnet test tests/PiSharp.Runtime.Tests/PiSharp.Runtime.Tests.csproj` green; `dotnet build PiSharp.sln` green.

---

## Phase C — Repair middleware `next` chain (review §6 #3 / finding 1.3)

**Objective:** middlewares compose as a real chain (each `next` invokes the following middleware), short-circuit once `Blocked`, and the first block decision sticks; `Blocked` is evaluated per link, not once after the loop.

### Task C1 — Red: chain semantics tests
- **Files:** `tests/PiSharp.Agent.Tests/Harness/LoopEventStageTests.cs` (extend; it already builds `HarnessEventContext` via `CreateContext` helper at ~328).
- **Change:** tests: (a) two middlewares with order-recording side effects → `next()` from the first must invoke the second (currently the first's `next` is `Task.CompletedTask`); (b) first middleware sets `Blocked = true` → second middleware must NOT run and the final `BeforeToolCallResult` must carry `Blocked=true` + reason; (c) `Blocked` set after `next()` returns still short-circuits downstream; (d) `AfterToolMiddleware` unaffected (modify path still applies).
- **Validate:** (a)–(c) fail pre-fix (middleware 2 runs today, block reason read after loop at line 26).
- **Outcome:** failing tests pinning real composition + short-circuit.

### Task C2 — Green: compose the chain
- **Files:** `src/PiSharp.Agent/Harness/LoopEvents/ToolMiddlewareStage.cs:21-26`.
- **Change:** build `next` by index: `Func<ExtensionMiddlewareContext, CancellationToken, Task> next = i == count-1 ? (_, _) => Task.CompletedTask : (ctx, ct) => middlewares[i+1](ctx, nextFor(i+1), ct);` invoke `middlewares[0](ctx, nextFor(0), ct)`. After each middleware returns (and inside the tail `next`), check `middlewareContext.Blocked` → break the loop (short-circuit). Keep the final decision write (lines 26-29) but base it on the sticky block flag.
- **Validate:** chain tests (a)-(d) green; existing permission-path tests (`tests/PiSharp.Permissions.Tests`, `AcpPermissionGateTests.cs`) green.
- **Outcome:** real pipeline semantics: order preserved, block short-circuits, downstream never runs after a block.

### Task C3 — Refactor: sticky decision + defensive guard
- **Files:** `src/PiSharp.Agent/Harness/LoopEvents/ToolMiddlewareStage.cs`.
- **Change:** once `Blocked` is observed, do not re-read `middlewareContext.Blocked` in later iterations (guard against a middleware un-setting it); document the sticky contract on `ExtensionMiddlewareContext` if not already there.
- **Validate:** re-run C1 tests + `PiSharp.Agent.Tests` full project.
- **Outcome:** deterministic, order-independent-of-observation decision.

**Phase C gate:** `dotnet test tests/PiSharp.Agent.Tests/PiSharp.Agent.Tests.csproj` green; `dotnet build PiSharp.sln` green.

---

## Phase D — Harden WS server (review §6 #4 / finding 3.5b)

**Objective:** bound per-message memory and concurrency, require explicit shutdown confirmation, fix the event-pump check-then-act race, and remove `!` null-forgiving over deserialized JSON.

### Task D1 — Red: server tests
- **Files:** `tests/PiSharp.Server.Tests/PiServerWebSocketHandlerTests.cs` (new or extend existing).
- **Change:** (a) send a >max-size text message → expect a close frame (e.g. `MessageTooBig`) and no dispatch; (b) 50 concurrent messages → at most `MaxConcurrentCommands` in-flight (instrument via `DispatchTextCommandAsync` gate); (c) `shutdown` without confirmation → `Fail` response; with confirmation → Ok; (d) two concurrent `attach` calls for the same session → exactly one event pump (observable via session `ReadEventsAsync` calls).
- **Validate:** all fail pre-fix (no limit today, shutdown always Ok at `:843`, `ContainsKey`+assign race at `:75-88`).
- **Outcome:** failing tests pinning each hardening target.

### Task D2 — Green: max message size
- **Files:** `src/PiSharp.Server/WebSockets/PiServerWebSocketHandler.cs:852-863` (`ReceiveTextAsync`), ctor options.
- **Change:** read into a bounded `MemoryStream` (e.g. `MaxMessageBytes` = 8 MiB configurable via server options); when `stream.Length > max` → close socket with `MessageTooBig` and return null; avoid `ToArray()` per message by `stream.ToArray()` only at end-of-message (keep, but bounded).
- **Validate:** D1(a) green.
- **Outcome:** authenticated-client OOM via unbounded message gone.

### Task D3 — Green: concurrency bound
- **Files:** `src/PiSharp.Server/WebSockets/PiServerWebSocketHandler.cs:96-114` (per-message `Task.Run`).
- **Change:** replace unbounded `Task.Run` dispatch with a `SemaphoreSlim(MaxConcurrentCommands)`-gated worker pool (or a bounded `Channel` of messages with N consumers); responses still serialized through `sendGate`.
- **Validate:** D1(b) green; existing server/daemon RPC tests green.
- **Outcome:** bounded dispatch; no unbounded thread growth per socket.

### Task D4 — Green: explicit shutdown confirmation
- **Files:** `src/PiSharp.Server/WebSockets/PiServerWebSocketHandler.cs:822-844` (`ShutdownAsync`).
- **Change:** `ShutdownRequest` gains required `Confirm: bool` (or `confirmToken`); without it → `Fail("confirmation_required")`. Update the daemon/CLI client that calls shutdown to send the flag (find callers via `lsp references` on `ShutdownAsync` / `ServerCommandTypes.Shutdown`).
- **Validate:** D1(c) green; client shutdown path still works end-to-end (`PiSharp.Server.DaemonCommands.Tests`, `PiSharp.Client.Tests`).
- **Outcome:** accidental shutdown impossible from a single malformed message.

### Task D5 — Green: event-pump `GetOrAdd` + null-forgiving removal
- **Files:** `src/PiSharp.Server/WebSockets/PiServerWebSocketHandler.cs:75-88` (`EnsureEventPumpAsync`), `:211/:248/:260` (and siblings).
- **Change:** `eventPumps.GetOrAdd(live.Id, _ => Task.Run(...))`; on the pump's `finally`, `TryRemove` the entry when the stored task is the same instance (re-creation allowed after failure). Replace `!` deserializations with `?? throw new InvalidOperationException(...)` / proper error responses so malformed payloads surface as `invalid_json`/`invalid_command` instead of `command_failed` NREs.
- **Validate:** D1(d) green; fuzz a few malformed payloads → typed failures.
- **Outcome:** one pump per session; deterministic command errors.

**Phase D gate:** `dotnet test tests/PiSharp.Server.Tests/PiSharp.Server.Tests.csproj` green; `dotnet build PiSharp.sln` green.

---

## Phase E — Memory backend index + dirty-flag flush (review §6 #5 / finding 4.1)

**Objective:** reads stop re-parsing the whole JSONL; writes batch via an in-memory index with a dirty flag and single flush per batch; atomic file writes retained.

### Task E1 — Red: write-count and read-consistency tests
- **Files:** `tests/PiSharp.Memory.Tests/FileMemoryProviderIndexTests.cs` (new).
- **Change:** (a) instrument via a probe: N=50 `PutAsync` calls → assert fewer than N full file writes happen (count `.tmp` renames by watching `records.jsonl` mtime / rename count through a temp-dir hook, or expose internal counter in test); (b) `PutAsync` then `GetAsync` returns the latest value without waiting for flush; (c) reload (`new FileMemoryProvider(...)`) after flush reflects all writes.
- **Validate:** (a) fails pre-fix (every `PutAsync` rewrites the file — see `FileMemoryProvider.cs:57-77`, `Save` at `:216-231`).
- **Outcome:** failing tests pinning batching + index semantics.

### Task E2 — Green: load-once index
- **Files:** `src/PiSharp.Memory.Backends.File/FileMemoryProvider.cs:42-55, 195-214, 57-77, 80-...` (per-op `Load` callers).
- **Change:** load each scope's `Dictionary<string, MemoryRecord>` once (lazily on first access, per scope); all reads (`GetAsync`, `SearchAsync`/list) serve from the in-memory index; keep the `_gate` semaphore for cross-thread consistency of the in-memory state, not per-read file I/O.
- **Validate:** existing `PiSharp.Memory.Tests` green + E1(b/c) green.
- **Outcome:** O(1) lookups; no per-op re-parse.

### Task E3 — Green: dirty-flag batch flush
- **Files:** `src/PiSharp.Memory.Backends.File/FileMemoryProvider.cs:216-262` (`Save`/`RegenerateSummary`) and mutators.
- **Change:** mutators set `_dirty = true`; a single `FlushAsync` (triggered on dispose, on explicit flush, or debounced) writes `records.jsonl` (temp+rename, existing atomic pattern `:228-230`) once and regenerates `memory_summary.md` once. Keep ordering by `RecordKey` (`:223`).
- **Validate:** E1(a) green (≤2 writes for 50 puts); crash-safety note: accept bounded loss of unflushed writes (documented tradeoff) or flush before returning on `Dispose`.
- **Outcome:** O(n) total write cost per batch instead of O(n²).

### Task E4 — Refactor: keep cross-process note
- **Files:** none (comment/ADR).
- **Change:** document that the in-process index assumes single-process access to a store directory (daemon-resident); if multi-process writers appear later, add file-locking + re-read-on-mtime-change. Note in `docs/pisharp-runtime.md` if behavior changes.
- **Validate:** docs-only; full `PiSharp.Memory.Tests` green.
- **Outcome:** documented boundary, no surprise multi-writer corruption.

**Phase E gate:** `dotnet test tests/PiSharp.Memory.Tests/PiSharp.Memory.Tests.csproj` green; `dotnet build PiSharp.sln` green.

---

## Phase F — Fail-closed unknown tools + gate extension spawns (review §6 #6 / findings 3.1+3.2) — *biggest security lever*

**Objective:** unknown (non-bash/write/edit) tools stop being allow-by-default in strict mode; the `rm -rf` detector stops being trivially bypassable; extension-provided `ExecAsync`/stdio-MCP spawns route through the approval lane (or are explicitly documented+opt-in).

### Task F1 — Red: permission tests
- **Files:** `tests/PiSharp.Permissions.Tests/PermissionsPolicyTests.cs` (extend) + `DangerousOpDetectorTests.cs` (extend).
- **Change:** (a) `Evaluate("mcp.foo.read", "{}", "none", headless:false)` in `strict` mode → `Deny` (already true at `PermissionsPolicy.cs:100-106` — pin it); in `prompt`/`automatic` mode → document current `Allow` and add a test for the *new* behavior (unknown tools → `Ask` in prompt mode when the tool name/args are classifiable, `Deny` in strict); (b) `BashCategoryOf("rm --recursive --force /")` → must NOT be `Bash`/`Allow` (today `RmRfPattern` at `DangerousOpDetector.cs:28` misses it); (c) flag-order variants (`rm -fr`, `rm -r -f`, `rm -rfv`) all detected.
- **Validate:** (b) fails pre-fix (returns `Bash` → `Ask` only because bash is ask-by-default; in `automatic` mode it would auto-allow → dangerous).
- **Outcome:** failing tests pinning the classification gaps.

### Task F2 — Green: robust bash classification
- **Files:** `src/PiSharp.Permissions/DangerousOpDetector.cs:28` (`RmRfPattern`), `:87-92` (`BashCategoryOf`).
- **Change:** tokenize the command (whitespace/quote-aware) and detect `rm` + any combination of `-r`/`--recursive`/`-f`/`--force` and `git reset --hard`/`git push` tokens; return a distinct category (`RmRf`) instead of collapsing into `GitPush` (also fixes 5.1 naming — update `PermissionsPolicy.cs:114-115` reason text accordingly).
- **Validate:** F1(b/c) green; existing bash-approval tests green.
- **Outcome:** flag-order variants blocked; categories truthful.

### Task F3 — Red+Green: schema-driven classification for custom tools
- **Files:** `src/PiSharp.Permissions/PermissionsPolicy.cs:108-119`; `src/PiSharp.Extensions/ExtensionRuntimeBinding.cs` / middleware caller that supplies `dangerousCategory` (find via `lsp references` on `Evaluate`).
- **Change:** extend classification: tools whose schema declares file-system args (`path`, `file`, `command`, `cwd`, `directory`) get conservative categories (e.g. `WriteOutsideCwd`-style checks when args resolve outside cwd); MCP tools (`mcp.<server>.<tool>`) with `command`/`exec` args → `Ask` in prompt mode, `Deny` in strict. Default for still-unclassifiable tools: `Ask` in prompt mode (not `Allow`), `Deny` in strict, keep `Allow` in `automatic` (documented UX tradeoff).
- **Validate:** new tests: unknown tool → `Ask` (prompt) / `Deny` (strict); MCP tool with `command` → `Ask`; existing permissions middleware tests (`tests/PiSharp.Permissions.Tests`) green.
- **Outcome:** fail-closed-by-posture for unknown tools without breaking automatic-mode UX.

### Task F4 — Gate extension `ExecAsync` + stdio MCP spawns
- **Files:** `src/PiSharp.Extensions/ExtensionRuntimeBinding.cs:41` (`ExecutionEnv` hand-off), `src/PiSharp.Mcp/McpServerRegistry.cs:14,37-41` (`ContributedServers`), `src/PiSharp.Mcp.Transports.Stdio/StdioTransportFactory.cs:18-25` (verbatim Command/Args).
- **Change:** (a) wrap the `IExecutionEnv` handed to extensions with an approval-checking proxy: `ExecAsync` calls route through the same permission decision the model path uses (block/ask/allow per policy), at least in strict mode; (b) extension-contributed stdio servers get a provenance tag (`sourceId`) surfaced to the permission gate, and `StdioTransportFactory` rejects/queues configs whose `Command` is not on an allow-list when strict; (c) document "any installed extension ≈ unapproved RCE" posture in `docs/pisharp-native-extensions.md` + `docs/pisharp-typescript-extensions.md` with the strict-mode gate as the mitigation.
- **Validate:** new tests in `tests/PiSharp.Mcp.Tests` (strict mode blocks unlisted contributed server command) + `tests/PiSharp.Extensions.Tests` (extension `ExecAsync` blocked in strict without approval); existing extension tests green.
- **Outcome:** extension spawns are gated or explicitly documented; posture visible in docs.

### Task F5 — Refactor: reduce silent no-op defaults (partial 1.2)
- **Files:** `src/PiSharp.Extensions/ExtensionRuntimeBinding.cs:46-128`.
- **Change:** (scope-limited) add a `BindingsComplete`/`ValidateBound()` that throws when the runtime failed to wire required capabilities (at least `ExecutionEnv`, `SendMessageAsync`, `ExecuteToolByNameAsync` non-default) — converts silent no-op failures into startup errors. Full Func-bag redesign is Phase J.
- **Validate:** existing `PiSharp.Extensions.Tests`/`PiSharp.TsBridge.Tests` green; a test asserts unbound runtime throws on use.
- **Outcome:** wired-or-fail instead of silent no-op for core capabilities.

**Phase F gate:** `dotnet test tests/PiSharp.Permissions.Tests/PiSharp.Permissions.Tests.csproj tests/PiSharp.Mcp.Tests/PiSharp.Mcp.Tests.csproj tests/PiSharp.Extensions.Tests/PiSharp.Extensions.Tests.csproj` green; `dotnet build PiSharp.sln` green. **UX risk flagged:** automatic-mode defaults unchanged; strict/prompt-mode only.

---

## Phase G — Atomic + protected OAuth storage (review §6 #7 / finding 3.3)

**Objective:** token writes become atomic (temp+rename under lock); corrupt files stop throwing on read; ACL tightening where cheap.

### Task G1 — Red: atomicity/corruption tests
- **Files:** `tests/PiSharp.Ai.Tests/FileOAuthStorageTests.cs` (new/extend).
- **Change:** (a) two concurrent `SetTokenAsync` for different providers → file remains valid JSON with both tokens (today `LoadMutableAsync` read-modify-write at `:195-200` + non-atomic `SaveAsync` at `:202-207` can tear); (b) seed a truncated/corrupt file → `GetTokenAsync` returns null + logs (today `JsonNode.Parse` at `:199` throws).
- **Validate:** both fail pre-fix (corrupt read throws; concurrent write can corrupt).
- **Outcome:** failing tests pinning the two defects.

### Task G2 — Green: lock + temp/rename + tolerant read
- **Files:** `src/PiSharp.Ai/Auth/FileOAuthStorage.cs:195-207`.
- **Change:** per-path `SemaphoreSlim` around read-modify-write; `SaveAsync` writes `path + ".tmp"` then `File.Move(tmp, path, overwrite: true)` (Windows-safe atomic replace); `LoadMutableAsync` wraps parse in try/catch → `[]` + warning log; tighten ACL on write (deny inheritance / restrict to user on Windows where `File.SetAccessControl` is available).
- **Validate:** G1(a/b) green; existing auth tests (`PiSharp.Ai.Tests`) green.
- **Outcome:** no torn/corrupt token files; no throw-on-corrupt; tokens still plaintext (DPAPI noted as optional follow-up, needs OS-specific handling).

**Phase G gate:** `dotnet test tests/PiSharp.Ai.Tests/PiSharp.Ai.Tests.csproj` green; `dotnet build PiSharp.sln` green.

---

## Phase H — Tolerate corrupt/unknown JSONL records (review §6 #8 / finding 2.5)

**Objective:** one bad line never bricks a store or the daemon; skip-and-log wins over throw.

### Task H1 — Red: corrupt-record tests
- **Files:** `tests/PiSharp.Coordination.Tests/CoordinationJsonlStoreTests.cs` (extend), `tests/PiSharp.Memory.Tests/FileMemoryProviderIndexTests.cs` (extend), `tests/PiSharp.Ai.Tests/FileOAuthStorageTests.cs` (covered in G1(b)).
- **Change:** coordination: file with a garbage line + an `unknown_type` record → `ReadAllAsync` returns the valid records, logs warnings, does not throw (today throws at `CoordinationJsonlStore.cs:60/65/87/91/99`). Memory: JSONL with one malformed line → `Load` skips it and keeps the rest (today throws at `FileMemoryProvider.cs:204-212`).
- **Validate:** both fail pre-fix.
- **Outcome:** failing tests pinning skip-and-log.

### Task H2 — Green: skip-and-log in both stores
- **Files:** `src/PiSharp.Coordination/CoordinationJsonlStore.cs:51-94`; `src/PiSharp.Memory.Backends.File/FileMemoryProvider.cs:200-213`.
- **Change:** per-line try/catch → log warning with line index + reason, continue. Coordination: unknown `type` → warn + skip (keep strict validation for known types). Memory: `JsonException`/`InvalidDataException` → warn + skip line.
- **Validate:** H1 tests green; daemon smoke test (`dotnet run` of coordination daemon with seeded bad tail, or existing `PiSharp.Advisor.Daemon.Tests`) starts successfully.
- **Outcome:** resilience to partial corruption; daemon starts despite a bad tail.

### Task H3 — Refactor: optional repair/compaction hook
- **Files:** `src/PiSharp.Coordination/CoordinationJsonlStore.cs` (note only).
- **Change:** document (comment) an optional `RewriteIfRepaired` on startup; do not implement now unless trivial. Keep `WriteLocks` eviction note: `ConcurrentDictionary` at `:14` grows per path — acceptable for daemon's single path; add `TryRemove` on store dispose as cheap hygiene.
- **Validate:** existing coordination tests green.
- **Outcome:** documented, no scope creep.

**Phase H gate:** `dotnet test tests/PiSharp.Coordination.Tests/PiSharp.Coordination.Tests.csproj tests/PiSharp.Memory.Tests/PiSharp.Memory.Tests.csproj` green; `dotnet build PiSharp.sln` green.

---

## Phase I — Fix `GrantStore` key encoding (review §6 #9 / finding 5.3)

**Objective:** `mcp.<server>.<tool>` grant keys round-trip instead of being unparseable.

### Task I1 — Red: round-trip tests
- **Files:** `tests/PiSharp.Permissions.Tests/GrantStoreTests.cs` (extend).
- **Change:** `KeyFor(Allow, "mcp.foo.read", "session-1")` → `TryParseKey` must yield back (`Allow`, `mcp.foo.read`, `session-1`) (today `TryParseKey` splits on `.` and requires exactly 4 parts → `mcp.*` keys unparseable; `GrantStore.cs:139-145`); legacy keys (`grant.allow.bash.sess1`) still parse.
- **Validate:** fails pre-fix for the `mcp.*` case.
- **Outcome:** failing test pinning the encoding defect.

### Task I2 — Green: length-prefixed/escaped encoding
- **Files:** `src/PiSharp.Permissions/GrantStore.cs:26, 109-114, 134-146`.
- **Change:** encode each component as `<length>:<raw>` segments (e.g. `grant.allow.10:mcp.foo.read.8:session-1` or base64url of components) so `.` is data, not delimiter; keep `Sanitize` for the legacy format; `TryParseKey` handles both new and legacy shapes (version tag `v2:` prefix).
- **Validate:** I1 green; existing `GrantStore`/`PermissionsMiddleware` tests green (find callers of `KeyFor`/`ListAsync` via `lsp references`).
- **Outcome:** grants for dotted tool names store/load/revoke correctly; legacy grants readable.

### Task I3 — Refactor: migration note
- **Files:** docs only (`docs/pisharp-runtime.md` or ADR).
- **Change:** document that pre-change keys stay readable and new keys use v2 encoding; no data migration needed.
- **Validate:** docs review.
- **Outcome:** no silent grant loss.

**Phase I gate:** `dotnet test tests/PiSharp.Permissions.Tests/PiSharp.Permissions.Tests.csproj` green; `dotnet build PiSharp.sln` green.

---

## Phase J — De-god `SessionRuntime` (review §6 #10 / findings 1.1+1.2) — DEFERRED, one quick win

**Objective:** out of scope for this run (weeks, highest regression risk). Fold in only the cheap correctness item; record the extraction as a follow-up.

### Task J1 — Verify and fix subscription lifecycle (quick win)
- **Files:** `src/PiSharp.Runtime/Runtime/SessionRuntime.cs:246-265` (`UnbindHarnessEventForwarding`), plus `Dispose`/`Bind*` callers via `lsp references`.
- **Change:** audit each `_bridge*Subscription` for double-dispose/leak. **Note:** the review's claim "`_bridgeSessionShutdownSubscription` disposed twice and never nulled (262-264)" does **not** match current code — it is disposed once at `:261` and nulled at `:264`. Verify the whole Bind/Unbind/Dispose lifecycle (including `BindHarnessEventForwarding` ~192-234) for any *actual* double-dispose or missed dispose; fix only what's real. If all clean, close with a test asserting `Unbind` is idempotent (calling twice must not throw).
- **Validate:** new idempotence test in `tests/PiSharp.Runtime.Tests` (or `PiSharp.Advisor.Daemon.Tests` which constructs `SessionRuntime`); dispose-twice path green.
- **Outcome:** subscription lifecycle proven safe; stale review claim corrected.

### Task J2 — Record de-god roadmap (no code)
- **Files:** docs (`docs/pisharp-developer-guide.md` architecture section or a new ADR).
- **Change:** outline extraction plan: (a) pull session/event-forwarding into a `HarnessEventBridge` service; (b) replace `ExtensionRuntimeBinding` Func-bag with a typed interface + `ValidateBound()` (already partially in F5); (c) shrink `CreateRuntimeAsync` (`PiRuntimeBootstrap.cs:31`) via a small composition root per subsystem.
- **Validate:** doc review only.
- **Outcome:** follow-up work is scoped for a future run.

---

## Tests & validation (global)

Per-phase gates (each phase lists its command). Final sweep, in order:

1. `dotnet build PiSharp.sln` — must compile with no new warnings.
2. `dotnet test tests/PiSharp.Agent.Tests/PiSharp.Agent.Tests.csproj` (Phases A, C).
3. `dotnet test tests/PiSharp.Runtime.Tests/PiSharp.Runtime.Tests.csproj` (B, J1).
4. `dotnet test tests/PiSharp.Server.Tests/PiSharp.Server.Tests.csproj` (D).
5. `dotnet test tests/PiSharp.Memory.Tests/PiSharp.Memory.Tests.csproj tests/PiSharp.Coordination.Tests/PiSharp.Coordination.Tests.csproj` (E, H).
6. `dotnet test tests/PiSharp.Permissions.Tests/PiSharp.Permissions.Tests.csproj tests/PiSharp.Mcp.Tests/PiSharp.Mcp.Tests.csproj tests/PiSharp.Extensions.Tests/PiSharp.Extensions.Tests.csproj` (F, I).
7. `dotnet test tests/PiSharp.Ai.Tests/PiSharp.Ai.Tests.csproj` (G).
8. Full suite: `dotnet test PiSharp.sln` — 100% green; no test modified to force a pass.

Manual smoke where relevant (each is a *run*, not a unit test):
- B: run a timeout exec from the CLI/TUI and confirm no orphan process remains (tasklist/ps).
- D: open two WS clients, `attach` same session concurrently; confirm single event stream; send >8 MiB message; confirm close frame.
- F: strict-mode session attempting an MCP tool with `command` → blocked with reason surfaced.

---

## Risks, tradeoffs, open questions

- **A (queues):** channel refactor could alter `QueueUpdate` emission timing (tests at `AgentHarnessEventTests.cs:170-178` assert counts). Mitigation: keep emission points; lock option preferred for minimal diff. Open: is FIFO across producers required? (assumed yes.)
- **B (kill):** `Kill(entireProcessTree: true)` on Windows may kill a process that already exited (races) — guard with try/catch; Unix `kill(-pgid)` needs `Process.StartInfo` process-group setup (setpgid not exposed directly by .NET — may require `setsid`-style wrapper or accepting tree-kill best-effort). Open: exact cross-platform orphan guarantee needed?
- **B (async readers):** switching off `OutputDataReceived` changes line-splitting behavior (CRLF handling) — keep `ReadLineAsync` semantics, verify no `\r` leakage in outputs.
- **C (middleware):** short-circuiting on first `Blocked` changes behavior for middlewares that today observe post-block state; extensions relying on "all middlewares always run" break. Mitigation: sticky-block documented; only `BeforeToolMiddleware` short-circuits.
- **D (WS):** adding required shutdown confirmation breaks older clients — coordinator/CLI must be updated in the same change (find via `lsp references`). Max-message size may need to be raised for large tool payloads; keep configurable.
- **E (memory backend):** dirty-flag flush trades durability for throughput — unflushed writes lost on hard crash; acceptable for memory records, but confirm with stakeholders whether `PutAsync` must be durable-before-return (then batch only the summary regen).
- **F (fail-closed):** biggest UX risk — unknown tools becoming `Ask` in prompt mode adds prompts for MCP/custom tools that were silently allowed. Mitigation: `automatic` mode unchanged; strict mode is the hard gate; document.
- **G (OAuth):** temp+rename on Windows is atomic only with `File.Move(overwrite:true)` on same volume; ACL tightening is Windows-only (guard by `OperatingSystem.IsWindows()`); DPAPI optional follow-up (needs scope decision).
- **H (JSONL tolerance):** skip-and-log can hide real corruption in production logs; mitigation: warning-level logs with line numbers + optional `RewriteIfRepaired` later.
- **I (grant keys):** legacy-format reader must not mis-parse v2 keys and vice versa; the `v2:` version tag avoids ambiguity.
- **J:** deferred; the review's double-dispose claim is stale in current code — do not "fix" what isn't broken; verify first.

---

## Appendix — finding confirmation status (as of 2026-08-15)

**Confirmed in source (this plan's basis):**

| Finding | Where verified | Status |
|---|---|---|
| 2.1 harness queue races | `AgentHarness.cs:29-33, 84-85, 91/98/105, 565, 585, 622-629, 688-714`; only `Interlocked` = dispose flag `:737`. Also `AgentState.cs:53-63` (`PendingMessageQueue` plain `Queue<>`, unguarded) and `Agent.cs:16,41-42,191` (`_listeners` plain `List<>`) — same race class in the steer path | Confirmed |
| 2.2 no kill on timeout/cancel | `SystemExecutionEnv.cs:159-203` (OCE catches `:191-198` return without `process.Kill`; zero Kill calls in file) | Confirmed |
| 2.3 unbounded capture + sync pipe read | `SystemExecutionEnv.cs:180-181, 212` (StringBuilder), `:217` `bytesCallback(...).GetAwaiter().GetResult()` in sync `HandleLine` | Confirmed |
| 1.3 middleware chain broken | `ToolMiddlewareStage.cs:21-24` (`next = (_, _) => Task.CompletedTask`), `:26` (Blocked read after loop) | Confirmed |
| 3.5b WS: no max size | `PiServerWebSocketHandler.cs:852-863` (8KB buffer + unbounded `MemoryStream`) | Confirmed |
| 3.5b WS: no concurrency bound | `PiServerWebSocketHandler.cs:96-114` (per-message `Task.Run`) | Confirmed |
| 3.5b WS: shutdown no confirmation | `PiServerWebSocketHandler.cs:822-844` (always Ok; comment "confirmation token is optional") | Confirmed |
| 3.5b WS: event-pump check-then-act | `PiServerWebSocketHandler.cs:75-88` (`ContainsKey` then indexer assign) | Confirmed |
| 5.2 `!` null-forgiving | `PiServerWebSocketHandler.cs:211, 248, 260` | Confirmed |
| 4.1 memory backend O(n)/O(n²) | `FileMemoryProvider.cs:28` (semaphore), `:42-55` (Load per read), `:195-262` (rewrite+summary) | Confirmed |
| 2.5 memory store throws on corrupt line | `FileMemoryProvider.cs:200-213` | Confirmed |
| 2.5 coordination store throws | `CoordinationJsonlStore.cs:56-92` (missing/blank/unknown type, deserialize), `:99` (timestamp) | Confirmed |
| 1.4 static `WriteLocks` never evicted | `CoordinationJsonlStore.cs:14, 31` | Confirmed |
| 3.1 permission fail-open for unknown tools | `PermissionsPolicy.cs:108-119` (default `Allow` at `:118`); strict-mode denies at `:100-106` (already correct — keep) | Confirmed (with nuance: strict already fails closed) |
| 3.1 `rm -rf` bypass | `DangerousOpDetector.cs:28` (`[a-zA-Z]*r[a-zA-Z]*f` misses `--recursive`/flag order) | Confirmed |
| 5.1 misleading category naming | `DangerousOpDetector.cs:90` (reset-hard/rm-rf → `GitPush`) | Confirmed |
| 3.1 only 3 tools classified | `DangerousOpDetector.cs:35-54` (bash/write/edit; default `None`) | Confirmed |
| 3.2 static MCP contributed servers | `McpServerRegistry.cs:14, 37-41` | Confirmed |
| 3.2 stdio spawn verbatim | `StdioTransportFactory.cs:20-21` | Confirmed |
| 1.2 Func-bag with silent no-op defaults | `ExtensionRuntimeBinding.cs:41` (ExecutionEnv settable), `:46-128` (defaults) | Confirmed |
| 3.3 plaintext non-atomic OAuth storage | `FileOAuthStorage.cs:195-207` (`WriteAllTextAsync`, no lock/rename) | Confirmed |
| 3.3 OAuth read throws on corrupt file | `FileOAuthStorage.cs:199` (`JsonNode.Parse` unguarded) | Confirmed |
| 5.3 grant-key encoding | `GrantStore.cs:26, 109-114, 139-145` (`.` allowed in Sanitize; `TryParseKey` splits on `.`, exactly 4 parts) | Confirmed |
| 3.4 session import path traversal | `SessionRuntime.cs:504-514` (`id` → dest filename), `:506, 520` (`cwd` from header) | Confirmed |
| 2.4 bridge queue full → fire-and-forget fallback | `SessionRuntime.cs:286-287` (`TryWrite` else `_ = WriteBridgeForwardingEventAsync(...)`) | Confirmed |

**Not fully verified / review-cited only (plan assumes review's citation; verify during execution):**

| Finding | Note |
|---|---|
| 1.1 "`_bridgeSessionShutdownSubscription` disposed twice and never nulled (262-264)" | **Does NOT match current code**: disposed once at `SessionRuntime.cs:261`, nulled at `:264`. Treat as stale; verify full Bind/Unbind/Dispose lifecycle in J1. |
| 1.1 god-object ctor ~20 params (`SessionRuntime.cs:25-48`) | File and member count corroborate the god-object shape; exact ctor param count not re-counted. |
| 1.4 static `ILogger` fields (`AgentLoop.cs:16-21`, `ToolCallExecutor.cs:17-22`) | Not read; review-cited. Not in ranked top 6 — optional cleanup only. |
| 1.5 `PiRuntimeBootstrap.CreateRuntimeAsync` ~555 lines (`PiRuntimeBootstrap.cs:31`) | Not read; review-cited. Deferred (J2). |
| 2.4 `ToolCallExecutor.cs:127` fire-and-forget, `SubagentSessionService.cs:164-166` `catch { }`, `PermissionsMiddleware.cs:120-123` session-name swallow | Not read; review-cited. Sweep in F/J if touching those files; otherwise follow-up. |
| 3.5 API key in query string (`ApiKeyValidator.cs:32-33`), key-unset silent 401s (`Program.cs:5`) | Not read; review-cited. Optional add-on to D if time permits. |
| 4.2-4.5 perf items | Not read; review-cited. Below ranked cutoff; E/F cover the top perf items. |
| 6.#10 de-god `SessionRuntime` | Partially confirmed (file size, member count); deferred by design. |

---

*End of plan. Next action after review/approval: execute Phase A Task A1 (red test) first.*
