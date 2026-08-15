# Task 1 Report — session-scoped UI bridge + configurable interactive timeout

**Status:** DONE
**Commit:** `a3120d5` — `feat(server): session-scoped UI bridge and configurable interactive timeout`

## Summary

Added a session-scoped overload to `PiServerUiBridge` so a command running on a specific
`LiveServerSession` can drive `select`/`input` UI on that session, plus an explicit per-call
response timeout. Implemented strictly to the brief's RESOLUTION: the overload takes an explicit
`TimeSpan? responseTimeout` per call; no `PiServerHostOptions` host option was added; the
`ServerUiBridge` constructor/DI registration was not touched.

## Required changes (per brief)

1. **`src/PiSharp.Server/UiBridge/IServerUiBridge.cs`** — gained
   `Task<ServerUiResponse> RequestUiAsync(ServerUiIntent intent, LiveServerSession target, TimeSpan? responseTimeout, CancellationToken ct = default)`.
   The existing `RequestUiAsync(intent, CancellationToken ct = default)` is unchanged (extension lane
   is preserved; callers binding to the old overload are unaffected).
2. **`src/PiSharp.Server/UiBridge/ServerUiBridge.cs`** —
   - Extracted the `RequestUiAsync` body into
     `RequestUiAsyncCore(ServerUiIntent intent, LiveServerSession? target, TimeSpan? responseTimeout, CancellationToken ct)`.
   - `EmitUiRequest(intent, target)` emits on `target ?? SelectSession()` (target threaded through
     instead of always `SelectSession()`); the existing `SelectSession()` fallback (latest session)
     is retained for the un-scoped overload.
   - Internal auto-cancel timeout is `responseTimeout ?? ResponseTimeout`.
   - Public overloads: `RequestUiAsync(intent, ct)` → core(intent, null, null, ct);
     `RequestUiAsync(intent, target, responseTimeout, ct)` → core(intent, target, responseTimeout, ct).
   - Theme interception behavior (`TryInterceptThemeRequestAsync`) unchanged — still runs before
     any client round-trip in the shared core.
3. **`src/PiSharp.Server/WebSockets/PiServerWebSocketHandler.cs`** — the private
   `NoOpServerUiBridge : IServerUiBridge` implementer gained the matching target-scoped member
   (returns a cancelled response), required for `IServerUiBridge` compliance; no behavior change.
   ⚠️ This one-line addition goes beyond the brief's three-file list but is mandatory: adding an
   interface member without implementing it in the existing implementer is a compile error. Minimal
   and consistent.
4. **`tests/PiSharp.Server.Tests/PiServerUiBridgeTests.cs`** (new) — mirrors the raw-web-socket
   client harness from `HostIntegrationTests.cs` (session creation + raw event lane reader answering
   via `ResolveUiAsync`, the exact resolution path the server's `ui_response` command invokes on the
   wire). Uses xUnit `[Fact]` per project convention.

## New tests

- **Test A** `Select_ThroughTargetScopedOverload_ResolvesToAnsweredValue` — a `select` kind request
  made through the **target-scoped** overload emits `ui_request` on the target session (asserted via
  the `requestId` in the emitted event) and, when the raw client sends `ui_response`
  (`ResolveUiAsync`), the request resolves to the answered value (`"beta"`, not cancelled).
- **Test B** `Select_GenerousResponseTimeout_DoesNotAutoCancelBeforeClientAnswers` — a 30s
  `responseTimeout` keeps the request pending past the bridge's fixed 5s auto-cancel window; the
  client answers after ~5.3s and the request resolves with the value (not auto-cancelled). Under the
  old fixed 5s timeout this scenario would have auto-cancelled, so it genuinely distinguishes the
  new behavior.

## TDD evidence

- **RED** — wrote `PiServerUiBridgeTests.cs`, ran
  `dotnet test tests/PiSharp.Server.Tests/PiSharp.Server.Tests.csproj --filter "FullyQualifiedName~PiServerUiBridgeTests" -m:1 --nologo`:
  compile failure `error CS1501: No overload for method 'RequestUiAsync'` at both call sites (target
  overload absent). Expected failure confirmed.
- **GREEN** — after implementing (interface + `RequestUiAsyncCore` + `EmitUiRequest` threading +
  NoOp implementer), same filtered run:
  `Passed! - Failed: 0, Passed: 2, Skipped: 0, Total: 2`.
- **Full project** — `dotnet test tests/PiSharp.Server.Tests/PiSharp.Server.Tests.csproj -m:1 --nologo`:
  `Passed! - Failed: 0, Passed: 64, Skipped: 0, Total: 64` (includes the 2 new tests; no sibling
  regressions).

## Other checks

- **Consistency:** `IServerUiBridge` has exactly two implementers (`ServerUiBridge`, private
  `NoOpServerUiBridge` in `PiServerWebSocketHandler.cs`); both now satisfy the interface. The full
  `PiSharp.Server` build (plus consumers `PiSharp.TsBridge` and `PiSharp.Server.Tests`) compiled
  with no errors, confirming cross-project interface compliance. The pre-existing `RequestUiAsync`
  overload is untouched, so all existing callers (PiSharp.Cli `InteractiveMode`, `RemoteTuiBackend`
  path via the websocket handler) bind as before.
- **Scope/discipline:** No `PiServerHostOptions` addition; bridge constructor/DI registration
  untouched; theme interception unchanged; `javascript/` untouched.
- **Stage hygiene:** committed exactly `src/PiSharp.Server/UiBridge/IServerUiBridge.cs`,
  `src/PiSharp.Server/UiBridge/ServerUiBridge.cs`,
  `src/PiSharp.Server/WebSockets/PiServerWebSocketHandler.cs`,
  `tests/PiSharp.Server.Tests/PiServerUiBridgeTests.cs`. Left unstaged: `src/PiSharp.Packages/obj/*`
  (build artifacts) and `.superpowers/` (this report). `BuiltInModels.g.cs` was not modified.

## Concerns

- Test B adds ~5.3s of deliberate wall-clock delay to the suite (small, but it is a sleep-based
  test) to prove the 30s timeout exceeds the fixed 5s auto-cancel. Under the old fixed timeout the
  same test fails, so it is a real regression guard — retained as-is per the brief's framing.
- The `NoOpServerUiBridge` target-scoped addition is technically outside the brief's enumerated
  file list but is a required consequence of the interface change (compile correctness); no
  behavior change.
- `[INFERENCE]` A live-client round-trip can't be exercised end-to-end on the DI-registered bridge
  because `PiServerHost` does not expose its internal `ServerSessionRegistry`/bridge; the test
  constructs the bridge directly (as `ServerUiBridgeThemeTests` already does) and resolves via
  `ResolveUiAsync` — the identical method the server's wire `ui_response` handler calls.

---

## Fix report — test-only hardening of session-scoped targeting (Task1Fix)

**Status:** DONE
**Fixes:** FINDING 1 (targeting regression guard) + FINDING 2 (hang hazard) — both in
`tests/PiSharp.Server.Tests/PiServerUiBridgeTests.cs` only; no production behavior touched.
**Verify:** `dotnet test tests/PiSharp.Server.Tests/PiSharp.Server.Tests.csproj -m:1 --nologo` →
`Passed! - Failed: 0, Passed: 64, Skipped: 0, Total: 64` (includes the updated tests; serial `-m:1`,
no formatter/full-suite run).

### FINDING 1 — a regression that ignores `target` would still pass before
The committed Test A created **one** session and read `ui_request` off it. Under a hypothetical
regression where the target-scoped overload fell back to the global most-recent `SelectSession()`,
the request would still land on that single session's lane and the test would pass — so it never
proved the session-scoped routing.

Fixed in `Select_ThroughTargetScopedOverload_EmitsOnTargetNotNewestSession_AndResolves`
(renamed to reflect the new guarantee):
- Creates a **second** session *after* `target`, and asserts `Assert.NotSame(target, newest)`
  where `newest = registry.Sessions.MaxBy(session => session.Id, StringComparer.Ordinal)` — the
  global fallback lane provably points at a *different* session.
- Reads the `ui_request` off **`target`**'s lane and asserts its `requestId` == the intent's
  `requestId`.
- Asserts **no** `ui_request` exists on the newer session's lane
  (`Assert.DoesNotContain(newest.EventLog.ReplayFrom(0).Events, e => e.Event.Type == "ui_request")`),
  so a fallback-to-newest regression fails loudly instead of passing.

Test B (`Select_GenerousResponseTimeout_DoesNotAutoCancelBeforeClientAnswers`) gained the same
second-session + `Assert.NotSame(target, newest)` + no-`ui_request`-on-newest guard for the 30s
timeout path.

### FINDING 2 — `CancellationToken.None` reader could hang the suite forever
The committed tests awaited `ReadEventsAsync(0, CancellationToken.None)` inline; if the
`ui_request` never arrived, the `await foreach` would block the suite indefinitely.

Fixed by extracting `ReadFirstUiRequestIdAsync(LiveServerSession, CancellationToken)` (returns the
first `ui_request`'s `requestId`, or `null` if the lane ends first) and wiring every reader to a
bounded token:
```csharp
private static readonly TimeSpan ReaderTimeout = TimeSpan.FromSeconds(15);
using var readerCts = new CancellationTokenSource(ReaderTimeout);
var targetRequestId = await ReadFirstUiRequestIdAsync(target, readerCts.Token);
Assert.Equal(intent.RequestId, targetRequestId);
```
A missing emission now cancels the reader after 15s and fails the test, instead of hanging.

### Stage hygiene
Staged: `tests/PiSharp.Server.Tests/PiServerUiBridgeTests.cs`, `.superpowers/` (this report).
Left unstaged: `src/PiSharp.Packages/obj/*` (build artifacts). `BuiltInModels.g.cs` unmodified.
