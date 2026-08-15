# Task 3 Report — CLI: daemon command host

## Status
Complete. `DaemonCommandHost` implemented in `src/PiSharp.Cli/Modes/DaemonCommandHost.cs`, tested via new `tests/PiSharp.Cli.Tests/Modes/DaemonCommandHostTests.cs`, full CLI test suite green, committed.

## Commit
- `a614029` `feat(cli): daemon command host wiring command/input/startup delegates`

## What was built
`public static class DaemonCommandHost` with `CreateHostOptions(string apiKey, ILoggerFactory? loggerFactory = null, Func<LiveServerSession?>? resolveSession = null)` returning `PiServerHostOptions` with every delegate wired to the session runtime:

- **RunCommandAsync** — builds `SlashCommandContext` exactly like `InteractiveMode.DispatchCommandAsync` (Select/Input/Notify via session-scoped `RequestUiAsync(intent, target, TimeSpan.FromSeconds(5*60), ct)`; `FileOAuthStorage` at `PiAgentPaths.FromCwd(...).AuthPath`; `SubmitPromptAsync: null`; session picker; `OAuthBrowserLauncher.OpenAsync`), executes via `SlashCommandRegistryFactory.Create(runtime).ExecuteAsync`, maps `SlashCommandResult` → `ServerCommandResult`. Debug logs command text only (no api keys/session ids).
- **CompleteCommandAsync** — `SlashCommandRegistry.Complete(text)` wrapped in `Task.FromResult` (Confirmed: `Complete` is synchronous, default `limit = 12`).
- **ProcessInputAsync** — bash `!`/`!!` lane via `DispatchUserBashAsync` (output/error concatenation identical to `InteractiveMode.ProcessInputAsync`), else `DispatchInputAsync(request.Text, request.Images, request.Source, ct)` → `ProcessInputResult`. Resolves the runtime via `resolveSession?.Invoke()?.Runtime` with `InvalidOperationException` fallback.
- **GetStartupMessagesAsync** — `new ServerStartupMessages(StartupResourceSummary.Create(session.Runtime))`.
- **PostStartupChecksAsync** — offline guard (`PI_OFFLINE` env / `SettingsSnapshot.Settings.Offline`), `NpmOutdatedChecker` + `OutdatedPackagesSummary.Format`, then private `CheckSelfUpdateAsync(SessionRuntime, Func<string, Task>, CancellationToken)` mirroring `InteractiveMode.CheckSelfUpdateAsync` (incl. `selfUpdate.checkOnStartup` read and the never-crash catch).
- **GetMcpStatusAsync** — returns the wire "unavailable" default `McpStatusResult([])` (no `Available` property exists in the contract; empty `Servers` is the unavailable shape). Verified by repo-wide grep: `PiSharp.Cli` has zero `Mcp` references and does not (and per the brief must not) reference the `PiSharp.Mcp` plugin assembly, so no status provider is reachable — the non-crashing default is the only correct implementation.
- Private UI helpers: `UiSelectAsync`, `UiInputAsync`, `UiNotifyAsync` (emits `system_message` via `AgentSessionEvent.FromServer`), `UiSelectSessionAsync` (loads via `loadAll`, renders `"{Id} {Path}"` labels through a `select` UI request, matches back by id/path, returns `current` when nothing matches or list is empty, `null` on cancel).

## TDD evidence
- **Red**: wrote the test first; `dotnet test --filter FullyQualifiedName~DaemonCommandHostTests` failed with `error CS0103: The name 'DaemonCommandHost' does not exist`.
- **Green**: after implementation, `DaemonCommandHostTests`: 2/2 passed.
- **Filter suite**: `dotnet test tests/PiSharp.Cli.Tests/PiSharp.Cli.Tests.csproj --filter "FullyQualifiedName~DaemonCommandHostTests|FullyQualifiedName~SlashCommandRegistryTests" --no-build --no-restore` → Passed! Failed: 0, Passed: 52, Total: 52.
- **Full suite (serial)**: `dotnet test tests/PiSharp.Cli.Tests/PiSharp.Cli.Tests.csproj --no-build --no-restore -m:1` → Passed! Failed: 0, Passed: 373, Skipped: 0, Total: 373.

Tests: (1) `RunCommandAsyncExecutesSlashCommandsAgainstSessionRuntime` — `ModeTestRuntime.CreateAsync()` runtime wrapped in `LiveServerSession`, `FakeUiBridge` (implements both `RequestUiAsync` overloads returning `ServerUiResponse(requestId)`, plus `ResolveUiAsync`), `/settings` executed → `Handled == true`, `IsError == false`, message contains `Current settings:` (no UI needed, no network); (2) `CreateHostOptionsWiresEveryCommandDelegate` — all six delegates non-null.

## Concerns / deviations from brief text (compilability-required)
1. **`PostStartupChecksAsync` emit adapter**: the brief body wrote `await emit(message)` and passed `emit` straight to `CheckSelfUpdateAsync(Func<string, Task>)`, but the server contract (verified in `PiServerHostOptions.cs:30` and `PiServerWebSocketHandler.cs:671`) types the parameter as `Action<string>` — `await emit(...)` is not compilable C#. Implemented as `emit(message)` (sync call, identical observable behavior) and `CheckSelfUpdateAsync(runtime, message => { emit(message); return Task.CompletedTask; }, ct)` so the private helper keeps the brief's `Func<string, Task>` signature mirroring `InteractiveMode`.
2. **`GetMcpStatusAsync`**: brief's "if a provider/manager exists" condition is false — no MCP provider is reachable from `PiSharp.Cli` (verified via grep + csproj references), so the minimal non-crashing default `McpStatusResult([])` is returned; `resolveSession` is not probed there because it could never contribute. `PiSharp.Mcp` remains unreferenced per global constraints.
3. **`UiSelectSessionAsync`**: `JsonlSessionMetadata` has no `PathOrId` property (verified in `PiSharp.Abstractions/Sessions/SessionMetadata.cs`); used `{Id} {Path}` labels and id/path matching instead, mirroring `ResumeSessionSlashCommand`.
4. `RunCommandAsync`'s `options` (`SlashCommandExecutionOptions?`) parameter is intentionally unused, as in the brief body.
5. `FakeUiBridge` satisfies both `RequestUiAsync` overloads per brief; the `--no-build` full-suite run used binaries from the preceding filter run (same commit state).

## Constraints honored
- Only `src/` and `tests/` touched; `javascript/` untouched.
- `PiSharp.Cli.csproj` references verified (PiSharp.Server, PiSharp.Runtime present — no new references added).
- `BuiltInModels.g.cs` and `*/obj/*` noise left unstaged; commit contains only the two task files (+240/-0).
- No api keys or session ids logged.

## Fix round (post-commit)

Fixes applied to `src/PiSharp.Cli/Modes/DaemonCommandHost.cs`:

- **FINDING 1 (Important)** — `UiSelectSessionAsync` renders select labels as `"{Id} {Path}"`, so `ServerUiBridge` echoes only one of those option strings back; the exact Id/Path match never hit and every selection silently fell back to `current`. Resolution now also accepts the full label form (`selected == $"{session.Id} {session.Path}"`, ordinal-ignore-case) so an echoed label resolves to the intended session. The no-match → return `current` fallback is preserved.
- **FINDING 2 (Minor)** — `RunCommandAsync` guarded with `string.IsNullOrWhiteSpace(text)` before the `text.Trim()[1..]` slice, returning graceful not-handled `new ServerCommandResult(false)` instead of throwing `IndexOutOfRangeException` on empty/whitespace run_command text. `CompleteCommandAsync` likewise guards empty/whitespace `text` before `Complete`, returning an empty `IReadOnlyList<string>` instead of passing it through.

Consistency check: the mirrored `text.Trim()[1..]` in `InteractiveMode.DispatchCommandAsync` is the in-process (TUI) input path, not the wire entry point, and `ResumeSessionSlashCommand` uses a different resolution mechanism — neither is a duplicate of the two findings and both were left untouched.

## Fix verification

Build (to compile the fix into binaries):

```bash
dotnet build tests/PiSharp.Cli.Tests/PiSharp.Cli.Tests.csproj --no-restore -m:1
```
→ 0 Error(s), 19 Warning(s) (pre-existing, none in `DaemonCommandHost.cs`).

Full CLI test suite, serial (mandated command):

```bash
dotnet test tests/PiSharp.Cli.Tests/PiSharp.Cli.Tests.csproj --no-build --no-restore -m:1
```

Output:

```
Test run for G:\code\AI\pi\PiSharp\.worktrees\daemon-runtime-hosting\tests\PiSharp.Cli.Tests\bin\Debug\net10.0\PiSharp.Cli.Tests.dll (.NETCoreApp,Version=v10.0)
VSTest version 18.0.1 (x64)
Starting test execution, please wait...
A total of 1 test files matched the specified pattern.
Passed!  - Failed:     0, Passed:   373, Skipped:     0, Total:   373, Duration: 2 s - PiSharp.Cli.Tests.dll (net10.0)
```

All 373 tests pass (0 failed, 0 skipped). `src/PiSharp.Ai/Models/Generated/BuiltInModels.g.cs` and `*/obj/*` left unstaged.