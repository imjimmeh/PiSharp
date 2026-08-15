# Adversarial Code Review — PiSharp

**Date:** 2026-08-15
**Method:** Autonomous code review (Hermes subagent), supplemented by spot-verification of the top findings against the actual source.
**Scope/honesty note:** ~40 core source files read end-to-end (agent loop, harness, runtime, server, CLI, permissions, MCP, TS bridge, memory backends, coordination, AI provider base, auth, plugin host) plus targeted greps. The solution was not built or run; findings marked as inferred where applicable. All paths are relative to the repo root.

## 1. Architecture (top 5)

**1.1 God object: `SessionRuntime`** — `src/PiSharp.Runtime/Runtime/SessionRuntime.cs:25-48`. Primary constructor with ~20 parameters (repo, harness factory, extension manager, plugin host, TS host, settings, model controller, telemetry, binding, theme, …), ~15 manually-managed `IDisposable` subscription fields (50–64), and 30+ public members. Hand-written `Bind/Unbind` pairs (`BindHarnessEventForwarding` 192–234, `UnbindHarnessEventForwarding` 246–265) are easy to get wrong; `_bridgeSessionShutdownSubscription` is disposed twice and never nulled (262–264).

**1.2 `ExtensionRuntimeBinding` is a 40+–property bag of settable `Func<>`s with silent no-op defaults** — `src/PiSharp.Extensions/ExtensionRuntimeBinding.cs:41-115`. Every capability defaults to `Task.CompletedTask` or null; nothing enforces that the runtime wired a capability. The extension host contract is untyped, mutable, and test-hostile.

**1.3 The middleware pipeline breaks the middleware contract** — `src/PiSharp.Agent/Harness/LoopEvents/ToolMiddlewareStage.cs:21-24`. Each middleware is invoked with `next = (_, _) => Task.CompletedTask`; all run in sequence against one shared mutable `ExtensionMiddlewareContext`; `Blocked` is read only *after* the loop (line 26). No composition, no short-circuit — the permission pipeline is order-dependent. *✓ Spot-verified (see Verification).*

**1.4 Global/static mutable state** — static `ILogger` fields set via `SetLogger` ceremony (`src/PiSharp.Agent/Loops/AgentLoop.cs:16-21`, `ToolCallExecutor.cs:17-22`); a static `ConcurrentDictionary<string, McpServerConfig> ContributedServers` in `src/PiSharp.Mcp/McpServerRegistry.cs:14,37-41`; a static never-evicted `WriteLocks` in `src/PiSharp.Coordination/CoordinationJsonlStore.cs:14,31`.

**1.5 Monolithic composition root** — `src/PiSharp.Runtime/Runtime/PiRuntimeBootstrap.cs:31` `CreateRuntimeAsync` is a ~555-line god method hand-wiring ~30 objects with `new` (no DI container). No real composition boundaries.

**Bonus (scalability):** the coordination daemon accepts one named-pipe client at a time (`maxNumberOfServerInstances: 1`, `CoordinationDaemon.cs:55-60`).

## 2. Correctness & robustness (top 5)

**2.1 Cross-thread races on harness queues/state — most serious.** `AgentHarness` keeps `_steerQueue/_followUpQueue/_nextTurnQueue/_pendingWrites/_listeners` as plain `List<>`s (`AgentHarness.cs:29-33,82-93`) mutated without locks. `Steer()` does `_steerQueue.Add` (91) from arbitrary threads (WebSocket handler `Task.Run` in `PiServerWebSocketHandler.cs:96-114,290`) while the loop drains via `ToArray(); queue.Clear()` with no lock (707–714). `_pendingWrites.Add` (196, 228, 565) vs `FlushWritesAsync` `ToArray()+RemoveRange` (622–629). Concurrent `Add`+`Clear` on `List<T>` is undefined behavior. *✓ Spot-verified: no lock or concurrent collection in the drain path (735).*

**2.2 Timed-out/cancelled bash commands are never killed.** `SystemExecutionEnv.ExecAsync` (`src/PiSharp.Runtime/IO/SystemExecutionEnv.cs:159-203`) catches `OperationCanceledException` and returns Timeout/Aborted (191–197) but never calls `process.Kill()`. *✓ Spot-verified: zero `process.Kill` calls in the file.*

**2.3 Unbounded stdout/stderr capture + sync-over-async pipe reader.** Output accumulates in unbounded `StringBuilder`s (180–181, 212); `OutputAccumulator` only bounds the displayed snapshot. `HandleLine` (209–219) calls `bytesCallback(bytes, …).GetAwaiter().GetResult()` (217) inside the stdout-reader event handler — blocking disk I/O on the pipe reader; a chatty child outpaces disk → the 64KB pipe buffer fills and stalls forever. *✓ Spot-verified (line 217).*

**2.4 Silent exception swallowing / fire-and-forget async.** `ToolCallExecutor.cs:127` `_ = emitAsync(...)` fire-and-forget tool events; `Task.WhenAll` in `ExecuteParallelAsync` (93); `SubagentSessionService.cs:164-166` `catch { }`; `PermissionsMiddleware.cs:120-123` swallows session-name errors (degrades to `"default"`); `SessionRuntime.cs:286-288` TS-bridge queue full → fire-and-forget fallback reorders events.

**2.5 Single corrupt record bricks a store.** `FileMemoryProvider.Load` (`FileMemoryProvider.cs:200-213`) throws on any malformed JSONL line; `CoordinationJsonlStore.ReadAllAsync` (56-69,87) throws on unknown record type (daemon fails to start); `FileOAuthStorage.GetTokenAsync` (`FileOAuthStorage.cs:14`) throws on corrupt auth file.

**Also:** `EnsureEventPumpAsync` check-then-act race (`PiServerWebSocketHandler.cs:75-88`); API key unset ⇒ silent 401s everywhere (`Program.cs:5` + `ApiKeyValidator.cs:20`); `!` null-forgiving over `JsonSerializer.Deserialize` in the WS handler (211, 248, 260, …).

## 3. Security (top 5)

**3.1 Permission gate is model-only and fail-open for unknown tools.** `PermissionsPolicy.Evaluate` (`PermissionsPolicy.cs:108-118`) allows any tool not in the rules matrix, not strict-mode, and not classified by `DangerousOpDetector`. `DangerousOpDetector.Category` (35-54) classifies only `bash`, `write`, `edit`; every custom/MCP/plugin tool is `None` ⇒ allowed by default. Name-based and trivially bypassed (`rm --recursive --force /` defeats `RmRfPattern`, line 28). `automatic` mode + `headlessDeny:false` broaden this further (132-151).

**3.2 Extensions bypass permissions entirely and can spawn processes.** `ExtensionRuntimeBinding.cs:41` hands out `IExecutionEnv` (full FS + `ExecAsync` bash); middleware gate only intercepts *model* tool calls. Extensions contribute stdio MCP configs with arbitrary `Command`/`Args` into the static registry (`McpServerRegistry.cs:37-41`) → `StdioTransportFactory` passes verbatim to process spawn (`StdioTransportFactory.cs:18-25`). TS extensions run as node children (`NodeTsBridgeClient.cs:41-51`). Installable by reference (`InstallExtensionAsync`, `PiServerWebSocketHandler.cs:682-690`). "Any installed extension = unapproved RCE." (Partly by design; undocumented and ungated.)

**3.3 Plaintext, non-atomic credential storage.** `FileOAuthStorage` persists API keys + OAuth refresh tokens in plaintext JSON (`FileOAuthStorage.cs:202-207`) — no encryption/DPAPI, default ACLs. `LoadMutableAsync` (195-199) read-modify-write with no lock; crash mid-write corrupts the file.

**3.4 Session import path traversal.** `SessionRuntime.ImportSessionFileAsync` (`SessionRuntime.cs:496-520`) parses `id` and `cwd` from the imported file's first line: `id` embeds into the destination filename (514-515) — `..` escapes via `File.Copy`; attacker-controlled `cwd` becomes the new session's working directory.

**3.5 Server/daemon trust boundaries.** Coordination named pipe has no auth/ACL (`CoordinationDaemon.cs:55-60`). WS server: API key in query string (`ApiKeyValidator.cs:32-33`), `shutdown` needs no confirmation (`PiServerWebSocketHandler.cs:822-844`), per-message `Task.Run` with no concurrency bound (96), `ReceiveTextAsync` (852-863) grows a `MemoryStream` with **no max-message-size limit** (authenticated-client OOM). `NativePluginHost` (`PluginHost.cs:16,25-31`) loads any DLL full-trust, no signing; `assembly.GetTypes()` can throw.

## 4. Performance (top 5)

**4.1 File memory backend O(n) per op / O(n²) bulk.** Every op re-reads/re-parses the whole JSONL and rewrites the whole file plus summary regen (`FileMemoryProvider.cs:195-262`); a process-wide `SemaphoreSlim` (28) serializes even reads; `SearchAsync` linearly scans (280-293).

**4.2 Sync-over-async.** `.GetAwaiter().GetResult()`/`.Result` in `InteractiveMode.cs:101,352,374,388`, `ExtensionManager.cs:216`, `TsExtensionHost.cs:931,968`.

**4.3 Unbounded queues/channels.** `EventStream` `Channel.CreateUnbounded` (`EventStream.cs:9`); `NodeTsBridgeClient._stderrLines` never-trimmed `ConcurrentQueue` (19,100-106); `McpServerSession` reconnect backoff exponential with no cap (`McpServerSession.cs:246-247`; attempt 30 at 1s base ≈ 17 years).

**4.4 Hot-path allocation churn.** `Agent.Reduce` rebuilds a `HashSet` per tool event (`Agent.cs:223-226`); `ProviderHttp.cs:140-156,78` double-copies payloads; WS receiver allocates 8KB buffer + `MemoryStream` + `ToArray()` per message.

**4.5 Repeated linear scans / full replays.** `ToolCallExecutor` rescans `context.Tools` per call (43, 113); `CoordinationDaemon` replays the entire event log on every startup (38-40) with no compaction; `PluginHost.IsUnloaded` force-runs up to 10 `GC.Collect()` (49).

## 5. Clarity / maintainability (top 5)

**5.1 Misleading security category naming.** `DangerousOpDetector.BashCategoryOf` maps `git reset --hard` and `rm -rf` to `GitPush` (`DangerousOpDetector.cs:90`).

**5.2 `!` null-forgiving everywhere** over deserialized JSON (`PiServerWebSocketHandler.cs` 211, 248, 260, …) → NREs surfaced as generic `command_failed`.

**5.3 Inconsistent grant-key encoding.** `GrantStore.KeyFor`/`Sanitize` allow `.` (`GrantStore.cs:26,109-114`) but `TryParseKey` splits on `.` and requires exactly 4 parts (139-145); MCP tools named `mcp.<server>.<tool>` (`McpToolAdapter.cs:50`) ⇒ every MCP-tool grant key is unparseable.

**5.4 Static-logger `SetLogger` ceremony + duplicated patterns.** `AgentLoop`/`ToolCallExecutor` global mutable logging; `Run`/`RunContinue`/`RunAgentLoopAsync`/`RunAgentLoopContinueAsync` quadruplet duplication.

**5.5 Over-complex core loop.** `AgentLoop.StreamAssistantResponseAsync` (219-354) mixes streaming/retry/abort/interceptor in a 135-line method with nested `while (true)` and 4 exit modes; `ApplyAssistantEvent`/`ApplyTextDelta`/`MergePartialContent` (356-444) reimplement delta accumulation with subtle invariants.

## 6. Recommended next-best refactorings (ranked by effort vs impact)

1. **Fix harness queue races (2.1)** — replace the steered queues/lists with locked or channel-based access; make `Subscribe`/`ToArray` consistent. `AgentHarness.cs`, `Agent.cs`. *~1 day. #1 latent crash source.* ✓ Verified.
2. **Kill children on timeout/cancel + bound output + non-blocking pipe read (2.2, 2.3)** — `process.Kill(entireProcessTree:true)` in OCE paths; async reader loop instead of `OutputDataReceived`+`GetResult`; cap capture magnitudes. `SystemExecutionEnv.cs`. *~1–2 days.* ✓ Verified.
3. **Repair middleware `next` chain (1.3)** — real chain composition, short-circuit on `Blocked`, sticky decision. `ToolMiddlewareStage.cs`. *~1 day + tests.* ✓ Verified.
4. **Harden WS server (3.5b)** — max message size, concurrency bound, explicit shutdown confirmation, `GetOrAdd` for event pumps. *~1 day.*
5. **Memory backend index + dirty-flag flush (4.1)** — load-once index, batch save/regenerate, cross-process locking. *~2–3 days.*
6. **Fail-closed unknown tools + gate extension spawns (3.1, 3.2)** — classify MCP/custom tools by schema; route extension `ExecAsync`/MCP spawns through the approval lane. *~2–3 days + UX; biggest security lever.*
7. **Atomic + protected OAuth storage (3.3)** — temp+rename under lock, tighten ACL / DPAPI. *~0.5 day.*
8. **Tolerate corrupt/unknown JSONL records (2.5)** — skip-and-log instead of throw. *Hours; big robustness win.*
9. **Fix `GrantStore` key encoding (5.3)** — length-prefixed/escaped scheme so `mcp.*` round-trips. *Hours.*
10. **De-god `SessionRuntime` (1.1/1.2)** — long-term: extract services, replacement for the Func-bag binding. *Weeks; do last.*

## Verification

Top three findings were independently spot-checked against the source after the subagent finished (not just relied on the subagent's self-report):

- **2.1 race:** `AgentHarness.cs` lines 29–32 (`List<>` fields), 91/98 (unlocked `Add`), 624–628 (`ToArray`+`RemoveRange`), 696–699/707 (`DrainQueueAsync(List<>)` with no lock); the only `Interlocked` use is the dispose flag (line 737). **Confirmed.**
- **1.3 middleware:** `ToolMiddlewareStage.cs` line 23 passes `(_, _) => Task.CompletedTask` as `next`; `Blocked` inspected only at line 26 after the loop. **Confirmed.**
- **2.2/2.3 no kill + blocking pipe:** `SystemExecutionEnv.cs` has no `process.Kill` call anywhere; OCE handled at 191/195 without kill; line 217 `bytesCallback(...).GetAwaiter().GetResult()` inside the stdout handler. **Confirmed.**

*Files modified by this review: none (analysis only). This document is the only write.*
