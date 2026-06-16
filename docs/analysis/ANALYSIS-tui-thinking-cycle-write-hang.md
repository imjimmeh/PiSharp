# TUI Thinking-Cycle Write Hang Analysis

Date: 2026-06-06
Author: OpenCode (GPT-5.4)
Status: Active investigation, not resolved

## Goal

Document the full investigation into the PiSharp TUI thinking-cycle bug, including:

1. Original user-visible symptoms.
2. What has been ruled out.
3. What code and logging changes were tried.
4. What the latest logs prove.
5. Where the current hang appears to be.
6. Recommended next steps.

## Executive Summary

The issue started as a TUI shortcut bug: `Shift+Tab` did not change the footer thinking level.

That turned out to be two separate problems:

1. Real `Shift+Tab` on the user's machine did not reliably reach PiSharp when Windows was forced onto Terminal.Gui `v2net`.
2. After remapping the shortcut to `Ctrl+Y`, the shortcut began reaching PiSharp and the runtime began computing real thinking-level transitions, but the footer still did not update.

The current evidence shows the second problem is not an input-routing bug. The live cycle now reaches:

1. `InteractiveMode` shortcut callback
2. `SessionRuntime`
3. `RuntimeModelController`
4. `AgentHarness.SetThinkingLevelAsync`
5. session append preparation
6. JSONL session storage header write
7. concrete filesystem write

The current live hang is no longer at key dispatch, model clamping, or footer rendering.

The latest evidence narrows the stall to the session `.jsonl` header write teardown path in the concrete execution environment. After experimentation, the observed hang moved from `FlushAsync` to `DisposeAsync` on the header write stream.

## User-Visible Symptoms

Initial symptom:

- `Shift+Tab` appeared to do nothing.
- Footer effort/thinking label stayed unchanged.

Later symptom after shortcut remap to `Ctrl+Y`:

- `Ctrl+Y` still appeared to do nothing visually.
- Footer continued to show the same thinking level even though logs later proved the runtime was cycling real levels.

Latest symptom after deep write-path instrumentation:

- Triggering thinking-cycle can make the app become unresponsive.
- Session header file bytes are written to disk, but the cycle does not complete and later cycles queue behind the first one.

## Scope

Areas investigated:

- `src/PiSharp.Tui`
- `src/PiSharp.Cli`
- `src/PiSharp.Runtime`
- `src/PiSharp.Agent`
- `tests/PiSharp.Tui.Tests`
- `tests/PiSharp.Cli.Tests`
- `tests/PiSharp.Runtime.Tests`
- `tests/PiSharp.Agent.Tests`
- local Terminal.Gui source mirrors under `G:\tmp\tgui` and `G:\tmp\tgui-nuget`
- live runtime logs under `C:\Users\jimme\.pi\PiSharp\logs\--G--code-AI-pi-PiSharp--`

## Timeline Summary

### Phase 1: `Shift+Tab` looked broken

Initial findings:

- `Shift+Tab` was registered as the built-in global thinking-cycle shortcut.
- `PromptEditor` intentionally removed its local `Shift+Tab` binding so the host/global path could own it.
- Footer rendering already used `state.ThinkingLevel`.
- TUI reducer already handled `ThinkingLevelSelect` and `ThinkingLevelChanged`.

Early hypotheses:

1. shortcut routing bug
2. footer render bug
3. non-reasoning model silently clamped to `Off`

One hypothesis was partially true in tests: non-reasoning models do make cycling a no-op. But the user later confirmed the live model was reasoning-capable.

### Phase 2: first real bug found in prompt/app routing

We found a real regression in `TuiInputRouter`:

- handled `Shift+Tab` could be dropped before global shortcut dispatch.

Changes made then:

- added red router regression test
- fixed router to reuse handled `Shift+Tab`
- added host-level regression coverage

That work was committed and merged as:

- `8355d6a fix: restore shift-tab thinking cycle`

But the live user bug remained.

### Phase 3: live logs disproved the first fix as the whole answer

New live diagnostics showed:

- no `Shift+Tab`-path logs at all in real sessions
- synthetic app-level `Key.Tab.WithShift` worked in tests

This proved the remaining live `Shift+Tab` issue was below PiSharp, in terminal-driver input translation.

### Phase 4: Windows driver investigation

We found PiSharp forces Windows onto `v2net` in:

- `src/PiSharp.Tui/Interactive/TuiConsoleDriverName.cs`

We added a temporary override via `PISHARP_TUI_DRIVER` and the user confirmed:

- letting Terminal.Gui choose the default driver made `Shift+Tab` work

That strongly implicated the Windows `v2net` input path for the original `Shift+Tab` symptom.

We then investigated why Windows was pinned to `v2net` in the first place and found repository evidence that the original reason was mobile/soft keyboard compatibility and character-stream input behavior.

### Phase 5: user chose to abandon `Shift+Tab`

User requested:

- revert the debug/driver experiments
- remap the built-in thinking-cycle shortcut to `Ctrl+Y`

That was implemented and committed as:

- `94dbbc8 fix: remap thinking shortcut to ctrl+y`

### Phase 6: `Ctrl+Y` still looked broken live

Fresh investigation on `Ctrl+Y` found a crucial difference from the old `Shift+Tab` bug:

- live `Ctrl+Y` reached PiSharp
- live `Ctrl+Y` resolved to `CycleThinkingLevel`
- live `Ctrl+Y` dispatched successfully

So the new problem was no longer input routing.

### Phase 7: runtime/model/state path proven live

Focused logging in `InteractiveMode` later proved the live model was real and reasoning-capable:

- model: `openai-codex/gpt-5.5`
- supported levels: `Off,Minimal,Low,Medium,High,XHigh`
- live cycle computations included:
  - `XHigh -> Off`
  - `Off -> Minimal`
  - `Minimal -> Low`
  - `Low -> Medium`

So the issue was not model clamping or a single-level model.

### Phase 8: footer/render path looked stale

We added broader permanent debug logs in:

- `TuiHarnessSubscription`
- `TuiRenderCoordinator`
- `TuiFooterSnapshotProvider`
- `FooterView`

Those logs showed:

- footer/render continued running
- footer repeatedly rendered `thinking=XHigh`
- no TUI thinking-event reduction logs appeared

That pointed at a missing event/update path rather than a footer cache bug.

### Phase 9: harness identity mismatch disproved

We added harness IDs to:

- `InteractiveMode`
- `TuiHarnessSubscription.Bind()`
- `AgentHarness.SetThinkingLevelAsync`

Live logs then showed:

- `InteractiveMode` and `TuiHarnessSubscription` used the same harness ID

So the bug was not caused by cycling one harness while the TUI subscribed to a different harness.

### Phase 10: event flow stall narrowed into session append path

We broadened the debug graph across:

- `SessionRuntime`
- `RuntimeModelController`
- `AgentHarness`
- loop event context/stages
- `TuiHarnessSubscription`

Live logs then showed the cycle reaching:

1. `InteractiveMode`
2. `SessionRuntime`
3. `RuntimeModelController`
4. `AgentHarness` state update

But not reaching:

- event publication
- listener notification
- TUI event receipt
- runtime completion logs

This narrowed the stall to the line after harness state mutation:

- `await _session.AppendThinkingLevelChangeAsync(...)`

### Phase 11: JSONL session append path narrowed the hang further

We added append-path logs in:

- `Session<TMetadata>`
- `JsonlSessionRepo`
- `JsonlSessionStorage`

That showed the first thinking-level append reached:

- session append prepared
- JSONL append starting
- header write starting

Later cycles only reached append preparation, which implied they were queuing behind the first append.

### Phase 12: concrete write seam narrowed it to header write I/O

We then added logs in `SystemExecutionEnv.WriteFileAsync(...)` and later split the session-only write into step-by-step markers.

This sequence narrowed the first stuck append from:

1. unknown header-write stall
2. to after `WriteAsync`
3. to `FlushAsync`
4. then, after skipping explicit flush, to `DisposeAsync`

That is the current stop point.

## What Has Been Proven

### Proven false

These are no longer good explanations:

1. `Ctrl+Y` never reaches PiSharp
2. shortcut resolver does not recognize the key
3. active model only supports `Off`
4. no session exists yet, so thinking cannot update in memory
5. TUI subscribed to a different harness than the one being cycled
6. footer cache alone is stale while state is correct everywhere else

### Proven true

1. Live `Ctrl+Y` resolves and dispatches as `CycleThinkingLevel`.
2. Live cycle computations on `openai-codex/gpt-5.5` move through real supported thinking levels.
3. `AgentHarness.SetThinkingLevelAsync` is entered live.
4. `_thinkingLevel` is updated live.
5. The first thinking-level change enters session append.
6. The first session append reaches JSONL header file write.
7. The header file bytes are actually written to disk.
8. Later cycles queue behind the first append.
9. Footer continues rendering stale `thinking=XHigh` because the first append never returns and later event publication never happens.

## Key Live Logs And What They Proved

### `2026-06-06T10-52-21-937_019e9c8f-fa2a-780c-9c80-ab4f2b1acb4d.log`

Proved:

- live model is `openai-codex/gpt-5.5`
- cycle computes real level transitions
- not a model-clamping bug

### `2026-06-06T11-16-29-645_019e9ca6-1147-7ee5-8090-59aebdceef9e.log`

Proved:

- footer/render path is running repeatedly
- footer still shows `thinking=XHigh`
- TUI state is not receiving reduced thinking events

### `2026-06-06T11-23-00-817_019e9cac-094b-704a-943b-5d872b4b9b1e.log`

Proved:

- `InteractiveMode` and TUI subscription use the same harness ID
- harness mismatch is not the explanation

### `2026-06-06T12-28-10-437_019e9ce7-b13d-7876-a4c1-12b04320a383.log`

Proved:

- cycle reaches `RuntimeModelController` before stalling
- no post-harness logs yet

### `2026-06-06T12-43-28-839_019e9cf5-b4c1-7fd6-9991-2a0b92fc0911.log`

Proved:

- cycle reaches `AgentHarness` state mutation
- then stops before any session-append log

### `2026-06-06T13-23-29-040_019e9d1a-5485-7ab3-b7a0-ed40ee7d5094.log`

Proved:

- first append reaches `JsonlSessionStorage.AppendEntryAsync(...)`
- first append reaches `JSONL session append starting ...`
- it does not reach `JSONL session header ensured ...`
- later cycles queue behind the first append

### `2026-06-06T14-17-46-123_019e9d4c-0782-7022-a50a-5a328e4be20e.log`

Proved:

- first append acquires `_writeGate`
- reaches `JSONL session header write starting ...`
- later cycles block before gate acquisition completes

### `2026-06-06T14-39-31-255_019e9d5f-f1b2-7fbd-92ef-90986323545b.log`

Proved:

- first append reaches `System execution env write bytes written ...`
- then stalls at or in `FlushAsync`
- header bytes still reach disk

### `2026-06-06T15-04-04-343_019e9d76-6bee-79c0-891e-62528e4ad960.log`

Proved:

- after removing explicit `FlushAsync`, the hang moves forward to `DisposeAsync()`
- the first append still never completes
- later cycles still queue behind it

## Files Changed During Investigation

### Shortcut and initial TUI fix work

- `src/PiSharp.Tui/Interactive/Input/TuiInputRouter.cs`
- `tests/PiSharp.Tui.Tests/TuiInputRouterTests.cs`
- `tests/PiSharp.Tui.Tests/TuiHostIntegrationTests.cs`
- `tests/PiSharp.Tui.Tests/TuiIntegrationTestHost.cs`

### Ctrl+Y remap commit

- `src/PiSharp.Tui/Interactive/BuiltInShortcuts/CycleThinkingLevelShortcutCommand.cs`
- `tests/PiSharp.Tui.Tests/TuiHostIntegrationTests.cs`
- `tests/PiSharp.Tui.Tests/TuiRenderingTests.cs`
- `tests/PiSharp.Tui.Tests/TuiShortcutTests.cs`

### Runtime/TUI/footer/harness/session/write-path diagnostics

- `src/PiSharp.Cli/Modes/InteractiveMode.cs`
- `src/PiSharp.Cli/Program.cs`
- `src/PiSharp.Runtime/Runtime/SessionRuntime.cs`
- `src/PiSharp.Runtime/Runtime/RuntimeModelController.cs`
- `src/PiSharp.Runtime/Runtime/PiRuntimeBootstrap.cs`
- `src/PiSharp.Runtime/IO/SystemExecutionEnv.cs`
- `src/PiSharp.Agent/Harness/AgentHarness.cs`
- `src/PiSharp.Agent/Harness/LoopEvents/HarnessLoopEventContext.cs`
- `src/PiSharp.Agent/Harness/LoopEvents/ExtensionDispatchStage.cs`
- `src/PiSharp.Agent/Harness/LoopEvents/ListenerNotificationStage.cs`
- `src/PiSharp.Agent/Sessions/Session.cs`
- `src/PiSharp.Agent/Sessions/JsonlSessionRepo.cs`
- `src/PiSharp.Agent/Sessions/JsonlSessionStorage.cs`
- `src/PiSharp.Tui/Interactive/Harness/TuiHarnessSubscription.cs`
- `src/PiSharp.Tui/Interactive/Shell/TuiRenderCoordinator.cs`
- `src/PiSharp.Tui/Interactive/FooterDataProvider.cs`
- `src/PiSharp.Tui/Interactive/Components/FooterView.cs`
- `src/PiSharp.Tui/Interactive/Shell/TuiShellView.cs`
- `src/PiSharp.Tui/Interactive/TuiHost.cs`

### Test and helper changes during investigation

- `tests/PiSharp.Cli.Tests/Commands/SlashCommandRegistryTests.cs`
- `tests/PiSharp.Cli.Tests/Modes/ModeTestRuntime.cs`
- `tests/PiSharp.Runtime.Tests/Runtime/SessionRuntimeTests.cs`
- `tests/PiSharp.Agent.Tests/Harness/AgentHarnessEventTests.cs`
- `tests/PiSharp.Tui.Tests/TuiApplicationContextTests.cs`
- `tests/PiSharp.Tui.Tests/TuiHostIntegrationTests.cs`
- `tests/PiSharp.Tui.Tests/TuiRenderingTests.cs`
- `tests/PiSharp.Tui.Tests/TestLogging/RecordingLoggerProvider.cs`

## Current Uncommitted Worktree State

At the time of writing, `git status --short` shows many active debugging changes, including runtime/agent/TUI instrumentation and test updates.

Notable current modified paths include:

- `src/PiSharp.Agent/Harness/*`
- `src/PiSharp.Agent/Sessions/*`
- `src/PiSharp.Cli/Modes/InteractiveMode.cs`
- `src/PiSharp.Cli/Program.cs`
- `src/PiSharp.Runtime/IO/SystemExecutionEnv.cs`
- `src/PiSharp.Runtime/Runtime/*`
- `src/PiSharp.Tui/Interactive/*`
- corresponding test files under `tests/PiSharp.*.Tests`

There is also a modified unrelated/generated model file currently in the worktree:

- `src/PiSharp.Ai/Models/Generated/BuiltInModels.g.cs`

This analysis does not rely on that file for the current hang diagnosis.

## Current Hang Location

### Best current diagnosis

For the first live thinking-level append in a session:

1. bytes are encoded
2. stream opens
3. bytes are written
4. header file appears on disk
5. completion does not return through the async stream teardown path

The latest experiment changed the stop point from:

- `FlushAsync()`

to:

- `DisposeAsync()`

That means the problem is not simply “flush is slow.” It is deeper in the async `FileStream` lifecycle used for this specific header write path.

### Most precise statement we can currently make

The first session-header write on the `.jsonl` session file appears to succeed materially at the filesystem level, but the async write/teardown path does not return to the caller, which prevents:

1. session append completion
2. harness own-event publication
3. TUI subscription delivery
4. footer state update

## Current Hypotheses

### Highest-probability hypothesis

The async `FileStream` write path used for session-header creation on this machine is hanging during async teardown for this specific file path and mode.

That could be caused by:

1. an OS/filesystem behavior specific to this path or handle mode
2. antivirus/indexer/observer interaction with the just-created session file
3. some interaction between async `FileStream` teardown and another process watching the session directory

### Lower-probability but still possible

1. cancellation token interaction around file teardown
2. a `FileStream` mode/share combination issue on first file creation
3. a path-specific interaction with the session directory structure under `~/.pi/agent/sessions/...`

## What We Have Not Yet Proven

We have not yet proven which of these exact variants is true:

1. async `DisposeAsync()` itself hangs
2. control never reaches `DisposeAsync()` body completion for some runtime reason around the stream
3. the specific `FileStream` construction options are the trigger
4. only async file creation hangs while synchronous close would succeed normally

## Recommended Next Step

The smallest next experiment is no longer “add more logs around the same async path.”

The best next move is:

1. bypass async stream teardown for the session header write only
2. use a plain synchronous file creation/write/close path for that one header write operation
3. leave later entry append behavior unchanged for now

Reason:

- the hang is already narrowed to the async stream completion path
- another logging step will likely only confirm the same boundary again
- a synchronous one-off header write is the fastest way to test whether the hang is an async `FileStream` lifecycle issue versus a broader session-path problem

## Suggested Concrete Follow-Up

Candidate narrow change:

- in the session-only header write path, replace async teardown with a fully synchronous write/close sequence for the header creation step only.

If that works, it would strongly suggest:

- the bug is not JSON serialization
- not path resolution
- not header content
- not gate acquisition
- not the later memory append or event publication logic
- but specifically the async file-write/teardown path for header creation

## Verification Notes

During this investigation, multiple focused tests and builds were run. Highlights:

- router/TUI regression slices passed during earlier shortcut work
- focused CLI/TUI/Agent/runtime logging tests were added and many passed
- some broader logging-test iterations were intentionally deprioritized later at user request
- recent targeted builds around logging-only changes passed for:
  - `src/PiSharp.Agent/PiSharp.Agent.csproj`
  - `src/PiSharp.Cli/PiSharp.Cli.csproj`

Known unrelated warnings seen at times:

- `src/PiSharp.Runtime/Runtime/SessionRuntime.cs` warnings such as `CS9124` and `CS9113`
- earlier unrelated TUI test warning noise

These warnings were not the root cause of the live thinking-cycle hang.

## Bottom Line

The investigation has moved from “shortcut seems broken” to a very narrow and concrete write-path hang.

Current best diagnosis:

- `Ctrl+Y` works
- runtime thinking-level cycling works
- harness state mutation works
- the first session thinking-level append blocks while completing the `.jsonl` session header write path
- after experiments, the current suspected stop point is async stream disposal/teardown, not dispatch, model selection, or footer rendering

The next useful step is to try a synchronous header write/close path for that one session-header creation seam.
