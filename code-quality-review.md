PiSharp Adversarial Code Review — Architecture & Code Quality

P0: Deadlock Surface (Correctness)

### 1. Session.cs — sync-over-async in property getters

File: src/PiSharp.Agent/Sessions/Session.cs:15,17

```csharp
public TMetadata Metadata => Storage.GetMetadataAsync().GetAwaiter().GetResult();
public string? LeafId { get => GetLeafIdAsync().GetAwaiter().GetResult(); set =>
Storage.SetLeafIdAsync(value).GetAwaiter().GetResult(); }
```

Why it matters: These are accessed from every code path that reads session metadata — including the TUI footer snapshot
provider (InteractiveMode.cs:113) which runs on the Terminal.Gui main loop. The MainLoopSyncContext posts continuations
back to the UI thread; .GetResult() blocks that thread → permanent deadlock. This is the same class as the known
remote-TUI freeze bug documented in docs/pisharp-remote-tui-model-switch.md.

Fix: Make Metadata and LeafId async-only. Expose GetMetadataAsync() and GetLeafIdAsync() as the public API. Remove the
synchronous properties entirely, or cache the result behind a Lazy<Task<T>> with a background refresh. Every caller must
be audited — this is a breaking contract change on ISession<TMetadata>.

### 2. InteractiveMode.cs:113 — sync block in UI footer

File: src/PiSharp.Cli/Modes/InteractiveMode.cs:113

```csharp
var sessionEntries = state.SessionBranchEntries ?? runtime.Session.GetBranchAsync().GetAwaiter().GetResult();
```

Why it matters: Runs inside a footer snapshot provider invoked from the TUI render loop. Same deadlock class as above. The
memory notes confirm the fix pattern: cache-backed background refresh + ConfigureAwait(false).

Fix: Pre-fetch branch entries asynchronously during session attach; cache them in ClientSessionState (remote) or in a
background task (local). The footer should read from the cache, not block on IO.

### 3. ExtensionManager.cs:216, ExtensionRegistry.cs:373, ExtensionRuntimeBinding.cs:273

Files: src/PiSharp.Extensions/ExtensionManager.cs:216, src/PiSharp.Extensions/ExtensionRegistry.cs:373,
src/PiSharp.Extensions/ExtensionRuntimeBinding.cs:273

```csharp
// ExtensionManager.cs:216
=> binding.GetRuleProviderNamesAsync(CancellationToken.None).GetAwaiter().GetResult();

// ExtensionRegistry.cs:373
=> _changeStream.PublishAsync(change, CancellationToken.None).GetAwaiter().GetResult();

// ExtensionRuntimeBinding.cs:273
=> binding.RegisterSkillProviderAsync(provider, CancellationToken.None).GetAwaiter().GetResult();
```

Why it matters: These are inside extension API shim classes (RuleApi, ExtensionRegistry.Publish, ExtensionApi). If any
extension calls these from a context where the SynchronizationContext is the UI thread (e.g., a prompt-contributor running
during TUI render), they deadlock. The CancellationToken.None also means there's no timeout — a blocked call hangs
forever.

Fix: Make these methods genuinely async (Task<IReadOnlyList<string>> GetRuleProviderNamesAsync() instead of synchronous).
For PublishAsync, use fire-and-forget with exception logging rather than blocking. For RegisterSkillProviderAsync, make it
async and await at the call site.

────────────────────────────────────────────────────────────────────────────────

P1: Static Logger Anti-Pattern (Testability + Correctness)

Files:

- src/PiSharp.Agent/Loops/AgentLoop.cs:16 — private static ILogger \_logger = NullLogger.Instance;
- src/PiSharp.Agent/Loops/ToolCallExecutor.cs:17 — same
- src/PiSharp.Agent/Resources/Prompting/PromptTemplateEngine.cs:15 — same
- src/PiSharp.Agent/Resources/SkillManager.cs:26 — same

Why it matters:

1.  Untestable: No way to inject a test logger; assertions on log output are impossible.
2.  Global mutable state: SetLogger() is a static mutator — concurrent tests race on it.
3.  Silent failures in production: If SetLogger() is never called (e.g., non-standard host), all logging falls to
    NullLogger — errors are invisible.

Fix: Convert each to instance-based logging. Pass ILoggerFactory through constructors. For static utility classes, accept
ILogger<T> via the calling context or make them non-static. This is a mechanical refactor but high-impact for testability.

────────────────────────────────────────────────────────────────────────────────

P2: SessionRuntime God Object (Architecture)

File: src/PiSharp.Runtime/Runtime/SessionRuntime.cs:25-48

23 constructor parameters. Owns: session repo, harness factory, session, extension manager, plugin host, TS bridge,
settings store, settings snapshot, model selection, resources, system prompt options, skills, extension binding,
diagnostics, prompt templates, theme, benchmark, extension load coordinator, logger factory, tools, auth storage,
telemetry.

The developer guide (docs/pisharp-developer-daemon-guide.md) explicitly flags this as the top refactor risk with a
deferred de-god roadmap. The roadmap items are sound but incomplete:

Missing from the roadmap:

- SessionRuntime also owns RuntimeSessionController, ExtensionSettingsService, ExtensionStateService,
  RuntimeExtensionReloader, and RuntimeModelController as inline fields — these should be extracted first as they're
  natural sub-systems.
- BindHarnessEventForwarding() (line 192) registers 7 separate event handlers with hardcoded "extension:ts-bridge" source
  IDs — this is a hidden coupling between SessionRuntime and the TS bridge that makes unit testing impossible without a
  real TsExtensionHost.
- The ExtensionBinding is both a constructor parameter and mutated via BindExtensionRuntime() — the binding lifecycle is
  unclear.

Fix priority: Execute the deferred de-god plan. Start with extracting RuntimeSessionController and
ExtensionSettingsService as their own types with explicit constructors. This alone reduces the ctor from 23 to ~15
parameters and makes the dependency graph visible.

────────────────────────────────────────────────────────────────────────────────

P3: Fire-and-Forget Task.Run Without Lifecycle Management

15+ sites across the codebase where \_ = Task.Run(...) is used without any tracked lifecycle:

┌─────────────────────────────┬───────────┬────────────────────────────────────────┬─────────────────────────────────────┐
│ File │ Line │ Context │ Risk │
├─────────────────────────────┼───────────┼────────────────────────────────────────┼─────────────────────────────────────┤
│ AgentLoop.cs │ 29, 54 │ Agent loop entry points │ Loop exceptions logged but never │
│ │ │ │ re-thrown; no way to await │
│ │ │ │ completion from caller │
├─────────────────────────────┼───────────┼────────────────────────────────────────┼─────────────────────────────────────┤
│ InteractiveMode.cs │ 181, 483, │ TS UI config, │ Stale cache if task faults; no │
│ │ 496, 511 │ shortcut/load-status/completion │ retry │
│ │ │ refresh │ │
├─────────────────────────────┼───────────┼────────────────────────────────────────┼─────────────────────────────────────┤
│ RemoteTuiBackend.cs │ 82, 83 │ Inbox processor, late-response drainer │ If these tasks die, the TUI │
│ │ │ │ silently stops receiving events │
├─────────────────────────────┼───────────┼────────────────────────────────────────┼─────────────────────────────────────┤
│ ClientWebSocketTransport.cs │ 90 │ WebSocket read loop │ Same — connection appears alive but │
│ │ │ │ events stop flowing │
├─────────────────────────────┼───────────┼────────────────────────────────────────┼─────────────────────────────────────┤
│ ClientSessionConnection.cs │ 24 │ Event pump │ Same class of silent failure │
├─────────────────────────────┼───────────┼────────────────────────────────────────┼─────────────────────────────────────┤
│ PiServerHost.cs │ 113 │ Host background task │ Daemon silently stops accepting │
│ │ │ │ connections │
├─────────────────────────────┼───────────┼────────────────────────────────────────┼─────────────────────────────────────┤
│ ServerSessionRegistry.cs │ 33 │ Idle-session sweep │ Memory leak — sessions never │
│ │ │ │ cleaned up │
├─────────────────────────────┼───────────┼────────────────────────────────────────┼─────────────────────────────────────┤
│ PiServerWebSocketHandler.cs │ 105, 328, │ Multiple command handlers │ Commands silently dropped if task │
│ │ 422, 1060 │ │ faults │
├─────────────────────────────┼───────────┼────────────────────────────────────────┼─────────────────────────────────────┤
│ TuiHost.cs │ 355, 393, │ Completion, hydration, post-startup │ TUI startup appears successful but │
│ │ 419 │ │ background work is lost │
├─────────────────────────────┼───────────┼────────────────────────────────────────┼─────────────────────────────────────┤
│ SessionRuntime.cs │ 278 │ Bridge forwarding worker │ TS events stop flowing to │
│ │ │ │ extensions │
├─────────────────────────────┼───────────┼────────────────────────────────────────┼─────────────────────────────────────┤
│ ContinuityScheduler.cs │ 82 │ Scheduler loop │ Continuity features silently │
│ │ │ │ disabled │
└─────────────────────────────┴───────────┴────────────────────────────────────────┴─────────────────────────────────────┘

Why it matters: Every one of these is a silent failure mode. When the task faults, the exception is either swallowed or
only visible in logs. The caller has no handle to await, retry, or monitor health. In the TUI context, this causes the
exact "input appears to do nothing" class of bugs documented in memory.

Fix: Introduce a BackgroundTaskTracker (or use IHostedService-style lifecycle) that:

1.  Tracks all background tasks with names and cancellation tokens
2.  Logs unhandled exceptions with context
3.  Provides a health signal (IsHealthy, LastException)
4.  Awaits all tasks on dispose with timeout

Apply to: RemoteTuiBackend, ClientWebSocketTransport, ClientSessionConnection, ServerSessionRegistry, SessionRuntime.

────────────────────────────────────────────────────────────────────────────────

P4: ExtensionRuntimeBinding Func-Bag (Design)

File: src/PiSharp.Extensions/ExtensionRuntimeBinding.cs:14-341

The binding is a class with ~20 nullable Func<> properties (GetSessionNameAsync, SetModelAsync, SetThinkingLevelAsync,
ExecuteToolByNameAsync, etc.) plus separate dictionaries for tools, skills, rules, providers. Many have silent no-op
defaults (e.g., GetSessionNameAsync = null → returns null).

Why it matters:

- Silent capability gaps: An extension requesting SetModelAsync gets a no-op when the host hasn't wired it, with no error.
  This masks misconfiguration.
- No compile-time guarantees: Missing capabilities are runtime nulls, not compile errors.
- Hard to test: Every test must construct a binding with all nullable fields explicitly set or accept silent no-ops.

The de-god roadmap already identifies this ("Replace the ExtensionRuntimeBinding Func-bag with a typed interface").
Execute it: define IExtensionRuntimeCapabilities with required vs optional members, and fail fast in ValidateBound()
(already partially implemented via BindingsComplete()).

────────────────────────────────────────────────────────────────────────────────

P5: Test Coverage Gaps

Ratio: 633 test files vs 1053 source files (~60%). But file count is misleading — depth varies enormously.

Known thin areas:

- Error paths in daemon-client wire: ClientEventReducer has tests, but gap-recovery edge cases (sequence gaps mid-batch,
  replay predating retained window, late responses arriving after timeout) have sparse coverage.
- Concurrency edge cases: AgentHarness concurrency (AgentHarnessConcurrencyTests.cs exists) but
  LiveServerSession.RunExclusiveAsync race conditions, RetainedEventLog gap detection under concurrent writers, and
  ExtensionRegistry change-stream ordering are weakly tested.
- Extension lifecycle: ExtensionBindingCompletionTests covers the validation, but failed extension loads, background
  activation timeouts, and TS bridge disconnect-reconnect scenarios lack integration tests.
- TUI remote path: RemoteTuiE2eTests exists but only covers happy path. The model-switch + ui_request cancel fault class
  (documented in docs/pisharp-remote-tui-model-switch.md) should have a regression test — it's a known fault that was
  fixed but has no explicit test guarding against recurrence.

Fix: Add targeted tests for:

1.  Gap recovery with replay predating retained window (ClientEventReducerTests)
2.  ui_cancelled envelope arriving during inline selection (InlineSelectionCoordinatorTests)
3.  LiveServerSession.RunExclusiveAsync with concurrent abort (ServerSessionRegistryTests or new)
4.  Extension load timeout causing graceful degradation, not crash (PiRuntimeBootstrapTests)

────────────────────────────────────────────────────────────────────────────────

P6: Broad Exception Swallowing

Notable sites:

- AgentLoop.cs:36,61 — catch (Exception exception) logs Warning and pushes to stream. The stream consumer may not handle
  errors; the exception is effectively lost.
- AgentHarness.cs:293,355,694 — compaction failure, skill execution failure, prompt hook patch failure all log Warning and
  continue. These are arguably correct (graceful degradation) but the log level should be consistent — some use
  LogWarning, some don't log at all.
- InteractiveMode.cs:116,400,432,450,468 — five separate catch (Exception) blocks in the footer/render path, all
  swallowing to Debug level. A tool execution exception or extension failure in the footer is invisible to users.
- PiServerWebSocketHandler.cs — multiple command handlers catch broad exceptions and return error responses, which is
  correct, but some paths (e.g., line 328 fire-and-forget) catch and discard.

Fix: Categorize: (1) failures that should propagate (wire protocol errors, auth failures), (2) failures that should
degrade gracefully with Warning-level log + user-visible signal, (3) failures that are truly best-effort (footer
snapshots). The current code mixes these categories.

────────────────────────────────────────────────────────────────────────────────

Prioritized Action Plan

┌──────────┬─────────────────────────────┬────────┬────────────────────┬─────────────────────────────────────────────────┐
│ Priority │ Area │ Effort │ Impact │ First Step │
├──────────┼─────────────────────────────┼────────┼────────────────────┼─────────────────────────────────────────────────┤
│ P0a │ Session.cs sync properties │ Small │ Prevents UI │ Make Metadata/LeafId async-only; audit all │
│ │ │ │ deadlock │ callers │
├──────────┼─────────────────────────────┼────────┼────────────────────┼─────────────────────────────────────────────────┤
│ P0b │ InteractiveMode.cs:113 sync │ Small │ Prevents UI │ Cache branch entries in background; read from │
│ │ block │ │ deadlock │ cache in footer │
├──────────┼─────────────────────────────┼────────┼────────────────────┼─────────────────────────────────────────────────┤
│ P1 │ Static loggers │ Medium │ Testability + │ Convert AgentLoop, ToolCallExecutor, │
│ │ │ │ correctness │ PromptTemplateEngine, SkillManager to instance │
│ │ │ │ │ logging │
├──────────┼─────────────────────────────┼────────┼────────────────────┼─────────────────────────────────────────────────┤
│ P2 │ SessionRuntime de-god │ Large │ Maintainability │ Extract RuntimeSessionController + │
│ │ │ │ │ ExtensionSettingsService as first cut │
├──────────┼─────────────────────────────┼────────┼────────────────────┼─────────────────────────────────────────────────┤
│ P3 │ Background task tracker │ Medium │ Reliability │ Introduce BackgroundTaskTracker; apply to │
│ │ │ │ │ RemoteTuiBackend, ClientWebSocketTransport, │
│ │ │ │ │ ServerSessionRegistry │
├──────────┼─────────────────────────────┼────────┼────────────────────┼─────────────────────────────────────────────────┤
│ P4 │ ExtensionRuntimeBinding │ Medium │ Correctness + │ Define IExtensionRuntimeCapabilities; migrate │
│ │ typed interface │ │ testability │ callers │
├──────────┼─────────────────────────────┼────────┼────────────────────┼─────────────────────────────────────────────────┤
│ P5 │ Regression tests for known │ Small │ Defensiveness │ Add tests for gap recovery, ui_cancelled, │
│ │ faults │ │ │ extension timeout degradation │
├──────────┼─────────────────────────────┼────────┼────────────────────┼─────────────────────────────────────────────────┤
│ P6 │ Exception categorization │ Medium │ Observability │ Audit all broad catches; promote silent │
│ │ │ │ │ failures to Warning + user signal │
└──────────┴─────────────────────────────┴────────┴────────────────────┴─────────────────────────────────────────────────┘

Verification: After each change, run dotnet test tests/PiSharp.TsBridge.Tests/PiSharp.TsBridge.Tests.csproj (parity
contract) and dotnet test PiSharp.sln (full suite). The pre-existing flaky timing tests under parallel load are not
regressions — see memory.
