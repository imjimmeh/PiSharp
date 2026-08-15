# Plan: Client-side logging for PiSharp (TUI / CLI / shared Client library)

Status: **Plan** (approved for implementation)
Date: 2026-08-15
Owner: PiSharp maintainers

## 1. Problem statement (verified)

The working assumption was: *"The tui/cli client has no logging; only the daemon does."*

**Verification result: the assumption is FALSE — the relationship is inverted.** Three read-only investigations (`src/PiSharp.Tui`, `src/PiSharp.Cli` + `src/PiSharp.Client`, `src/PiSharp.Server`) found:

- The **TUI** (`src/PiSharp.Tui`) is saturated with structured `ILogger` calls — ~60 emit sites across 16 files (`TuiHost.cs`, `ExtensionUiBridgeHost.cs`, `TuiInputRouter.cs`, `TuiCommandController.cs`, `TuiHarnessSubscription.cs`, `PromptEditor.cs`, etc.). What it lacks is its **own provider**: it never builds an `ILoggerFactory` and depends on the host (`PiSharp.Cli`) injecting one.
- The **CLI** (`src/PiSharp.Cli`) owns the **only file-logging implementation in the repository**: `Logging/CliFileLogging.cs`, `Logging/RollingFileLoggerProvider.cs`, `Logging/JsonFileLoggerProvider.cs`, `Logging/JsonLogFormatter.cs`. It builds a `LoggerFactory` with Debug + rolling-file providers at `Program.cs:75-81` and passes it into the TUI host in both local and remote paths.
- The **daemon** (`src/PiSharp.Server`) contains **no logging provider of its own**. When run standalone (`Program.cs`, `WebApplication.CreateBuilder`), it logs to console via ASP.NET defaults only. Its file log exists **only because the CLI hosts it**: `DaemonMode.cs:86-92` builds the same CLI file-logging factory and injects it into `PiServerHost` via `PiServerHostOptions.LoggerFactory` (`PiServerHost.cs:37-38`).
- Log destination is shared by all three: `~/.pi/PiSharp/logs/pi.log` (confirmed `C:\Users\jimme\.pi\PiSharp\logs\pi.log`), with per-session retargeting to `logs/<encodedCwd>/<session>.log` (`CliFileLogging.cs:43,56-66,103-113`).

So: the client stack **does** log today when launched through the CLI. The real gaps are (a) providers that are CLI-only and internal, (b) a TUI that silently no-ops when no factory is injected, (c) several silent paths in the shared Client library and headless CLI modes, (d) a standalone daemon with console-only logging, and (e) no redaction policy in the logging path.

## 2. Goals

1. Every PiSharp process (TUI, CLI, daemon, and any host of `PiSharp.Client`) writes structured logs to `~/.pi/PiSharp/logs/` by default.
2. Logging infrastructure is shared, public, and reusable — not `internal` to the CLI.
3. No diagnostic is lost to a silent `catch` or a no-op error handler in the client stack.
4. Sensitive data (API keys, conversation content) is never written at Information+ level.
5. Debugging flows stay non-blocking: no sync-over-async file IO on the TUI UI thread (Terminal.Gui `MainLoopSyncContext` deadlock hazard — the known freeze root cause).

## 3. Design

### 3.1 Shared logging library

Extract the CLI's `Logging/` directory into a new project **`src/PiSharp.Logging`** (public types), referenced by `PiSharp.Cli`, `PiSharp.Client`, `PiSharp.Tui`, and `PiSharp.Server`.

- Move: `CliFileLogging.cs`, `RollingFileLoggerProvider.cs`, `JsonFileLoggerProvider.cs`, `JsonLogFormatter.cs` (all currently `internal` in `src/PiSharp.Cli/Logging/`).
- Make public: `CliFileLogging` (static entry), `RollingFileLoggerProvider`, `RollingFileLoggerOptions`, `RollingFileMode`, `JsonFileLoggerProvider`, `JsonLogFormatter`.
- Keep behavior identical: `logging.file/level/maxFiles/json` settings keys (`PiLoggingSettings.cs:5-20`), env overrides `PISHARP_LOG_FILE|LEVEL|MAX_FILES|FORMAT` (`CliFileLogging.cs:8-11`), defaults Debug level / 7 retained files / `~/.pi/PiSharp/logs/pi.log`.
- `PiSharp.Logging` references `PiSharp.Compatibility` (for `PiLoggingSettings`, `PiSettingsConfiguration`, `PiAgentPaths`).
- Reference-graph check: `PiSharp.Cli → PiSharp.Logging`; `PiSharp.Client → PiSharp.Logging`; `PiSharp.Tui → PiSharp.Logging`; `PiSharp.Server → PiSharp.Logging`. No cycles (Cli/Client/Tui/Server all currently point down into Server/Runtime/Tui; adding Logging below all of them is safe).

### 3.2 Provider fallback in the TUI

`TuiHostOptions.LoggerFactory` (`src/PiSharp.Tui/Interactive/TuiHostOptions.cs:72,83`) is nullable; when null, every component falls back to `NullLogger` silently. Change:

- In `TuiHost.RunAsync` (`src/PiSharp.Tui/Interactive/TuiHost.cs`), if `options.LoggerFactory` is null, build one via `CliFileLogging.CreateConfiguredFileLogging(cwd)` — so a bare `TuiHost` (tests, embedding) still writes to the shared log directory instead of dropping diagnostics.
- Route `TuiShortcutController`'s `ReportError` callback (`TuiHost.cs:187` currently wires `_ => { }`) to the logger.

### 3.3 Client library: inject loggers everywhere

`PiSharp.Client` today accepts an optional `ILogger` in only two classes (`ClientWebSocketTransport.cs:58`, `RemoteTuiBackend.cs:73`); every other class is silent. Standardize on `ILoggerFactory` (or `ILogger<T>`) constructor injection:

- `ClientWebSocketTransport`, `RemoteTuiBackend`, `ClientSessionConnection`, `DaemonDiscovery`, `DaemonLauncher`, `DaemonLeaseStore`, `ClientEventReducer`, `ClientToTuiAdapter` (last two only if they gain I/O or error surfaces — pure reducers may stay).
- Update all construction sites: `src/PiSharp.Cli/Modes/InteractiveMode.cs:281-288` (already passes loggers), `src/PiSharp.Cli/Modes/DaemonMode.cs:134`, `src/PiSharp.Cli/Modes/StatsMode.cs:67`, `src/PiSharp.Sdk/PiSharpClient.cs:114,155` — pass the active `ILoggerFactory`.
- No `ILogger?`-defaults-to-null going forward: constructor takes a required factory; hosts that have none build one from `PiSharp.Logging`.

### 3.4 Level policy

| Level | Content |
|---|---|
| `Information` | lifecycle: process start/exit, mode entry, daemon connect/disconnect, session create/attach, log-file path |
| `Debug` | per-command wire traces, event dispatch, refresh cycles, routing decisions |
| `Warning` | timeouts, retries, malformed frames, late responses, recovered gaps, swallowed-catch sites that currently return null/false |
| `Error` | unhandled exceptions, loop crashes, connect failures, hydration failures |

No `Console.WriteLine` diagnostics in TUI code (console belongs to Terminal.Gui); all TUI diagnostics go through `ILogger`.

## 4. Implementation steps (in order)

### Step 1 — Extract `src/PiSharp.Logging`
1. Create `src/PiSharp.Logging/PiSharp.Logging.csproj` (net10.0 classlib; refs: `PiSharp.Compatibility`).
2. `git mv src/PiSharp.Cli/Logging/*.cs src/PiSharp.Logging/`; change `internal` → `public` on the six types.
3. Add project reference `PiSharp.Logging` to `PiSharp.Cli`, `PiSharp.Client`, `PiSharp.Tui`, `PiSharp.Server` csproj files.
4. Update `src/PiSharp.Cli` usings (same namespaces if kept; otherwise adjust).
5. Add `src/PiSharp.Logging.Tests` (move any existing CLI logging tests from `tests/PiSharp.Cli.Tests` if they cover the provider).
6. **Verify**: `dotnet build PiSharp.sln`; `dotnet test tests/PiSharp.Cli.Tests` and new Logging.Tests green.

### Step 2 — TUI provider fallback + shortcut error routing
1. `src/PiSharp.Tui/Interactive/TuiHost.cs:187` — replace `_ => { }` `ReportError` with logger-backed handler.
2. `TuiHost.RunAsync` — null-factory fallback to `PiSharp.Logging` file factory.
3. Add `ILogger` to `TuiShortcutController` (`RefreshExtensionShortcutsAsync` / `BuildCore`) so cache-refresh failures are logged (was: no-op `ReportError`).
4. **Verify**: `dotnet test tests/PiSharp.Tui.Tests`; build green.

### Step 3 — Client library logger injection
1. Constructor changes in `PiSharp.Client` classes listed in §3.3 (required `ILoggerFactory`).
2. Add the missing call sites from §5 (timeouts, malformed frames, late lane, subscriber exceptions, discovery/lease failures).
3. Update construction sites: `InteractiveMode.cs` (already done), `DaemonMode.cs:134`, `StatsMode.cs:67`, `PiSharp.Sdk/PiSharpClient.cs:114,155`.
4. **Verify**: `dotnet test tests/PiSharp.Client.Tests`; `dotnet test tests/PiSharp.Cli.Tests` (covers interactive remote path).

### Step 4 — CLI/headless-mode + daemon lifecycle logging
1. `src/PiSharp.Cli/Program.cs` — log process start (version, args, cwd, profile, mode, resolved log path) at Information; log exit code + duration at exit. Move factory creation above early-exit paths (version/package/check-updates) or accept those stay console-only.
2. `Modes/InteractiveMode.cs` — Information-level connect/session-create/attach results; Warning on cursor persistence failures (`ReadCursorSequence`/`WriteCursorSequence`).
3. `Modes/RpcMode.cs`, `AcpMode.cs`, `PrintMode.cs`, `SubagentJsonMode.cs`, `StatsMode.cs` — mode entry at Information, agent-failure exits at Error, metrics failure at Warning.
4. `Modes/DaemonMode.cs` — Information for daemon start (pid/port), health-timeout Warning, stop, stale-lease clear.
5. `src/PiSharp.Server` — connection accept/disconnect at Information in `PiServerWebSocketHandler.HandleHttpAsync` (currently unlogged); standalone `Program.cs` wires `PiSharp.Logging` file factory so a bare daemon writes `pi.log`.
6. **Verify**: `dotnet build PiSharp.sln`; `dotnet test PiSharp.sln`.

### Step 5 — Redaction + documentation
1. Redaction: never log the API key (`ApiKeyValidator.cs:33` uses `access_token` query param); scrub query strings in any URL logging; never log conversation/prompt content at Information+; document the rule in `PiSharp.Logging`.
2. Docs: document `PISHARP_LOG_FILE|LEVEL|MAX_FILES|FORMAT` env knobs and the `logging.*` settings keys in `docs/pisharp-developer-guide.md` (they exist today, `CliFileLogging.cs:8-11`, `PiLoggingSettings.cs:5-20`).
3. **Verify**: build + tests green; manual smoke test below.

## 5. Logging call sites to add (grounded inventory)

### PiSharp.Client
1. `ClientWebSocketTransport.ConnectAsync:68-78` — Information connect attempt/result; Error on `WebSocketException`/refused.
2. `ClientWebSocketTransport.SendCommandCoreAsync:109-112` — Warning per command timeout (type, id, effective timeout). Today returns `ServerResponse.Fail("timeout")` silently.
3. `ClientWebSocketTransport.ReadLoopAsync:199-201` — already Error on loop crash; add Information on clean loop exit and on Close with non-empty `_pending`.
4. `ClientWebSocketTransport.ReadLoopAsync:182` — replace `Debug.WriteLine` (malformed frame) with Warning (survives outside a debugger).
5. `ClientWebSocketTransport.ResolveResponse:248-263` — Debug for the `_late` lane (timed-out response arriving late).
6. `ClientSessionConnection.PumpEventsAsync:80-88` — Error when a subscriber throws (currently swallowed at 87).
7. `DaemonDiscovery.IsDaemonAvailableAsync:9-23` — Debug health-check result / failure reason.
8. `DaemonLauncher.WaitForHealthyAsync:11-29,31-45` — Debug/Error for spawn failures and health-timeout (currently return null silently).
9. `DaemonLeaseStore.ReadAsync/TryReadAsync:14-33, WriteAsync:49-67, ClearAsync:36-47, ProcessAlive:81-87` — Debug/Warning for corrupt lease, write failure, cleanup failures.
10. `RemoteTuiBackend.ProcessInboxAsync:585-604` — already Error on apply failure; add Information on subscribe/unsubscribe and on `RecoverFromGapAsync:224-266` completion.
11. `RemoteTuiBackend.DisposeAsync:203-221` — Debug teardown with final `LastAppliedSequence`.

### PiSharp.Cli
12. `Program.cs:75-81` — Information startup banner: version, args summary, cwd, profile, mode, log file path (path discoverability).
13. `Program.cs:99-107` — Debug/Information for daemon discovery/auto-start result and the `daemon unavailable; falling back` transition (106).
14. `Program.cs:126` — Information when the session log file is retargeted (new path).
15. `Program.cs:169` — Information exit: exit code + total runtime.
16. `Modes/InteractiveMode.cs:299-357` — Error wrapping connect/session-create/attach failures (lease port, attachId, serverSessionId).
17. `Modes/InteractiveMode.cs:378-466` — Debug on successful refresh (first fill / counts) alongside existing failure Debugs (407,424,441).
18. `Modes/InteractiveMode.cs:537` — Warning when cursor persistence fails (resume breakage is silent today).
19. `Modes/RpcMode.cs` / `AcpMode.cs` / `PrintMode.cs` / `SubagentJsonMode.cs` — Information mode entry; Debug per prompt/submit; Error on agent-failure exits (`PrintMode.cs:38`). File logger already built at this point.
20. `Modes/StatsMode.cs:40-48,67-70` — Warning/Error for get_metrics failure (console-only today).
21. `Modes/DaemonMode.cs:29-81,110-167` — Information/Warning for daemon start (pid/port), health-timeout (76), stop (147), stale-lease clear.
22. `Parsing/CliParser.cs` — optional Warning copy of parse diagnostics (currently stderr-only via `Program.cs:30`).

### PiSharp.Tui
23. `Interactive/TuiHost.cs:42-52` — Debug driver name + init failure (`Application.Init`).
24. `Interactive/TuiHost.cs:64-71` — Debug keybindings.json path + parsed-binding count on load; Warning on reload failure.
25. `Interactive/TuiHost.cs:187` — `ReportError` wired to logger (see §3.2).
26. `Interactive/TuiShortcutController.cs:40-60` (`RefreshExtensionShortcutsAsync`) and `:63-90` (`BuildCore`) — Debug refresh start/binding count; Warning invalid/conflicting skips; Error refresh failure.
27. `Interactive/Keybindings/KeybindingsWatcher.cs` — Debug reload trigger/success/failure/diag count.
28. `Interactive/TuiHost.cs:491-506` — Information shutdown start/completion + durations (currently unlogged `finally`).
29. `Interactive/TuiHost.cs:449` — Error for the "Failed to connect to the daemon" signal (currently UI-only at 450) with session/daemon context.
30. `Interactive/ExtensionUiBridgeHost.cs` — Debug overlay open/close (`OpenCustomUiOverlay`/`CloseCustomUiOverlay`).
31. `Interactive/Components/TuiTranscriptInteractionController.cs:90-96` — promote clipboard failure from Debug to Warning (user-visible action failed silently).
32. `Interactive/Shell/TuiRenderCoordinator.cs:237` — extend existing Warning with last-success timestamp/count for polling-health diagnosis.

### PiSharp.Server
33. `WebSockets/PiServerWebSocketHandler.cs:51` (`HandleHttpAsync`) — Information connection accept/disconnect (serverSessionId, remote endpoint, duration).
34. `Program.cs` — wire `PiSharp.Logging` file factory for standalone runs.
35. `Authentication/ApiKeyValidator.cs` — ensure no URL/query logging includes `access_token` (redaction guard, see Step 5).

## 6. Out of scope (explicit)

- Changing the wire protocol or daemon RPC surface.
- Telemetry/OTLP export (exists separately: `src/PiSharp.Telemetry.Otlp`).
- Altering existing log call sites that already work (they stay as-is; only additions listed above).
- Modifying the reference-only `javascript/` directory.

## 7. Risks & edge cases

- **UI-thread deadlock**: any file IO performed synchronously on the TUI UI thread can deadlock Terminal.Gui v2 (`MainLoopSyncContext` posts continuations to the blocked thread). File provider writes are async-safe today; new TUI call sites must not block on log writes. Rule: TUI code never does sync file IO for logging.
- **Behavior change in log output**: moving providers to a new assembly must not change `pi.log` format or rotation (tests in `PiSharp.Cli.Tests` / new `PiSharp.Logging.Tests` pin this).
- **Silent fallback in embedded hosts**: any host that constructs `TuiHost` / `ClientWebSocketTransport` without a factory now gets a real file logger — verify no test relies on `NullLogger` (update those tests to assert on the injected factory instead of relying on absence of output).
- **Redaction regressions**: `access_token` in query strings must not appear in any new log line; add a guard test.
- **Log volume**: Debug default writes per-command traces; existing 7-file retention bounds disk usage. No change to retention policy.

## 8. Verification (final acceptance)

1. `dotnet build PiSharp.sln` — clean.
2. `dotnet test PiSharp.sln` — full suite green (no test modified to pass; existing expectations preserved).
3. Manual smoke:
   - `pisharp` (interactive TUI via CLI) → `~/.pi/PiSharp/logs/pi.log` (or per-session file) contains startup banner, mode entry, connect, per-command Debug, shutdown.
   - `pisharp daemon start` → daemon process logs appear in `pi.log` with connection accept lines.
   - Standalone `dotnet run --project src/PiSharp.Server` → writes `pi.log` (Step 4.5).
   - Kill a daemon mid-session → client logs timeout Warning and late-response Debug, no deadlock (Ctrl+C still works).
4. Grep the log dir for `access_token` → absent.
