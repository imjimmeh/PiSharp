# PiSharp TUI Tracing Guide

This guide shows how to capture and analyze a `dotnet-trace` recording for PiSharp TUI typing performance, including arbitrary prompt-editor typing, not just `@file` completion.

Use this guide when:

- typing in the prompt editor feels laggy,
- you want to know whether the hot path is in PiSharp or `Terminal.Gui`,
- you want a reproducible workflow for comparing plain typing, multiline typing, and completion-heavy typing.

## What This Guide Covers

There are two useful workflows:

1. **Live PiSharp trace**
   Run the real PiSharp TUI under `dotnet-trace`, type anything you want in the prompt editor, then inspect the trace.

2. **Reproducible test trace**
   Run the real-host TUI test scenarios under `dotnet-trace` when you want repeatable traces for plain typing, multiline growth, or completion-heavy typing.

Use the live workflow when you want to explore an ad-hoc typing pattern. Use the test workflow when you want an apples-to-apples comparison across code changes.

If you want to isolate prompt-editor behavior from loaded extensions, skills, prompt templates, themes, and context files, run the live workflow with `--no-resources`.

## Prerequisites

### Install `dotnet-trace`

If `dotnet-trace` is not already installed:

```powershell
dotnet tool install -g dotnet-trace
```

If you already have it and want the latest version:

```powershell
dotnet tool update -g dotnet-trace
```

Verify installation:

```powershell
dotnet-trace --version
```

### Build PiSharp First

```powershell
dotnet build PiSharp.sln
```

### Keep The Scenario Narrow

For useful traces:

- capture only a few seconds around the typing burst,
- avoid submitting the prompt unless you are investigating turn startup,
- test one hypothesis at a time,
- keep a note of exactly what keys you typed.

## Workflow 1: Trace A Live PiSharp Prompt Editor Session

This is the best workflow for "typing anything in the prompt editor."

### Step 1: Create an output directory

```powershell
$traceDir = Join-Path $PWD ".trace"
New-Item -ItemType Directory -Force -Path $traceDir | Out-Null
```

### Step 2: Start PiSharp under `dotnet-trace`

Use a normal interactive PiSharp startup command. If you already have a working model configured, use it. Example:

```powershell
dotnet-trace collect `
  --profile dotnet-sampled-thread-time,dotnet-common  `
  --output (Join-Path $traceDir "prompt-editor-live.nettrace") `
  -- dotnet run --project src/PiSharp.Cli -- --model <your-model>
```

Minimal-resource variant for debugging:

```powershell
dotnet-trace collect `
  --profile dotnet-sampled-thread-time,dotnet-common  `
  --output (Join-Path $traceDir "prompt-editor-live-no-resources.nettrace") `
  -- dotnet run --project src/PiSharp.Cli -- --model <your-model> --no-resources
```

Notes:

- Replace `<your-model>` with a model that already works in your environment.
- Add `--no-resources` when you want to remove resource-loaded extensions, skills, prompt templates, themes, and context files from the startup/profile picture.
- If you only care about typing and not model turns, you can stop before submitting any prompt.
- If you prefer to trace an already-running PiSharp process, use the attach workflow in the appendix.

### Step 3: Type the scenario you want to study

Examples:

- plain text typing: `hello world this is a typing burst`
- cursor movement and edits: type, move left/right, backspace, insert
- multiline growth: use `Shift+Enter` between lines
- completion-heavy typing: `/he`, `@src/`, or other completion triggers
- paste: paste a medium-sized multi-line block

Suggested rule: do not press plain `Enter` unless you intentionally want to trace prompt submission. Use `Shift+Enter` for multiline prompt growth.

### Step 4: Stop the trace

When you have finished typing, stop the traced process with `Ctrl+C` in the tracing terminal, or exit PiSharp cleanly and let `dotnet-trace` finish writing the `.nettrace` file.

## Workflow 2: Trace Reproducible Real-Host TUI Tests

PiSharp now has real-host TUI typing scenarios that run the actual `TuiHost` on `Terminal.Gui`'s fake driver. These are useful when you want repeatable captures.

### Plain prompt typing

```powershell
dotnet-trace collect `
  --profile dotnet-sampled-thread-time,dotnet-common  `
  --output (Join-Path $traceDir "prompt-typing-test.nettrace") `
  -- dotnet test tests/PiSharp.Tui.Tests/PiSharp.Tui.Tests.csproj --filter "RealHostPromptTypingBurstKeepsUiResponsive|RealHostPromptTypingBurstDoesNotExplodeTranscriptOrLayoutWork"
```

### Multiline prompt growth

```powershell
dotnet-trace collect `
  --profile dotnet-sampled-thread-time,dotnet-common  `
  --output (Join-Path $traceDir "prompt-multiline-test.nettrace") `
  -- dotnet test tests/PiSharp.Tui.Tests/PiSharp.Tui.Tests.csproj --filter RealHostMultilineTypingBurstTracksPromptHeightWithoutExcessiveLayoutWork
```

### Completion-heavy prompt typing

```powershell
dotnet-trace collect `
  --profile dotnet-sampled-thread-time,dotnet-common  `
  --output (Join-Path $traceDir "prompt-completion-test.nettrace") `
  -- dotnet test tests/PiSharp.Tui.Tests/PiSharp.Tui.Tests.csproj --filter RealHostCommandCompletionTypingBurstMeasuresCompletionChurn
```

### File-reference completion probe with named counters

```powershell
$env:PISHARP_RUN_TUI_PROFILING_TESTS = "1"
dotnet-trace collect `
  --profile dotnet-sampled-thread-time,dotnet-common  `
  --output (Join-Path $traceDir "prompt-file-reference-probe.nettrace") `
  -- dotnet test tests/PiSharp.Tui.Tests/PiSharp.Tui.Tests.csproj --filter OptionalRealHostTuiProfilingProbeCapturesNamedCounters --logger "console;verbosity=detailed"
```

This test prints PiSharp-side counters such as render cycles, layout applies, completion counts, file-system calls, and git-visibility calls. Use those counters next to the trace so you can tell whether the hot path is expected to be completion-related before you dive into call stacks.

## How To Analyze The Trace

## First Pass: Ask A Narrow Question

Before opening the trace, decide what you want to answer:

- Is plain typing expensive even with no completions?
- Does multiline growth trigger repeated layout work?
- Does `/` or `@` typing trigger synchronous completion work?
- Is the hot path inside PiSharp or inside `Terminal.Gui`?

If you ask all of those questions at once, the trace becomes harder to interpret.

## Second Pass: Open The Trace In A Viewer

If your `dotnet-trace` version supports conversion to Speedscope format:

```powershell
dotnet-trace convert `
  --format speedscope `
  --output (Join-Path $traceDir "prompt-editor-live.speedscope.json") `
  (Join-Path $traceDir "prompt-editor-live.nettrace")
```

Then open the converted file in https://www.speedscope.app/.

If you prefer native Windows tooling, open the `.nettrace` file in PerfView or Visual Studio's profiler tooling.

## Third Pass: Map Hot Frames To PiSharp Concepts

Use this table when reading stacks:

| If hot frames are in...                                                                                      | Likely meaning                                                             |
| ------------------------------------------------------------------------------------------------------------ | -------------------------------------------------------------------------- |
| `Terminal.Gui.TextView`, `PromptEditor`, cursor/layout math                                                  | plain text editing or wrapping is expensive                                |
| `TuiRenderCoordinator.Render`, `TuiLayoutMetrics`, `TuiShellView.ApplyLayout`                                | layout churn is expensive                                                  |
| `ChatView.Render`, `ChatRowRenderPipeline`, transcript row rendering                                         | prompt typing is causing transcript rerender work                          |
| `TuiHostHandlers.CompletePrompt`, `PromptFileReferenceCompletionProvider`                                    | completion is hot                                                          |
| `FileReferenceEntryEnumerator`, `IFileReferenceFileSystem`, `IGitVisibilityService`, `System.IO` enumeration | file-reference completion is spending time on filesystem or git visibility |

## What Different Results Usually Mean

### Case 1: Plain typing is hot in `Terminal.Gui.TextView`

This points toward prompt-editor editing, wrapping, or native `TextView` behavior rather than PiSharp transcript rendering.

Look for:

- `Terminal.Gui.TextView`
- `PromptEditor`
- cursor/offset mapping
- wrap-related string work

### Case 2: Plain typing is hot in PiSharp render/layout paths

This points toward PiSharp invalidating or recalculating more UI than necessary.

Look for:

- `TuiRenderCoordinator.Render`
- `TuiLayoutMetrics.PromptContentHeight`
- `TuiShellView.ApplyLayout`
- repeated render cycles during a short burst

### Case 3: Prompt-only typing is hot in transcript rendering

This suggests prompt edits are causing unnecessary transcript work.

Look for:

- `ChatView.Render`
- transcript row rendering
- `ChatRowRenderPipeline`
- row-materialization and width recalculation work

### Case 4: `@` or `/` typing is hot in completion and filesystem stacks

This suggests synchronous completion work is the bottleneck.

Look for:

- `TuiHostHandlers.CompletePrompt`
- `PromptFileReferenceCompletionProvider`
- `FileReferenceEntryEnumerator`
- `System.IO` enumeration
- git visibility enumeration

In the current PiSharp profiling probe, the strongest signal already found was filesystem-heavy file-reference completion work rather than transcript rerender churn.

## How To Compare Scenarios

Use the same process for three traces:

1. plain typing
2. multiline typing with `Shift+Enter`
3. completion-heavy typing such as `@src/`

Then compare:

- total time spent in `Terminal.Gui.TextView`
- total time spent in PiSharp layout/render methods
- total time spent in completion methods
- whether transcript rendering appears at all during prompt-only typing

That comparison is often more useful than trying to reason from one trace in isolation.

## Practical Trace Tips

- Prefer Release builds if you are comparing absolute timings across runs.
- Prefer the real app when you want to type arbitrary keys manually.
- Prefer the real-host test scenarios when you want reproducibility.
- Keep trace windows short. A focused 3-10 second capture is easier to analyze than a long session.
- Keep one note per capture: what you typed, whether completions were visible, whether you used `Shift+Enter`, and whether you submitted the prompt.

## Common Mistakes

- Tracing a full conversation turn when you only meant to study typing.
- Pressing plain `Enter` and accidentally mixing typing cost with request/agent cost.
- Comparing traces with different input scenarios.
- Ignoring the PiSharp-side probe counters when a completion-heavy scenario is already telling you where to look.

## Recommended Investigation Order

1. Run the always-on real-host tests to confirm the regression suite is green.
2. Run the opt-in probe test if the slow path involves `@` or `/` completions.
3. Capture a manual live trace for the exact prompt-editor behavior that feels slow.
4. Compare that trace to one reproducible real-host test trace.

If the manual live trace is noisy, capture a second live trace with `--no-resources` and compare the two before changing code. 5. Only then decide whether to optimize PiSharp code or investigate `Terminal.Gui` internals.

## Appendix: `dotnet-trace` Quick Reference

### Attach To An Existing PiSharp Process

List candidate processes:

```powershell
dotnet-trace ps
```

Collect from a PID:

```powershell
dotnet-trace collect `
  --profile dotnet-sampled-thread-time,dotnet-common  `
  --process-id <pid> `
  --output (Join-Path $traceDir "attached-pisharp.nettrace")
```

### Capture A Different Profile

Start with `dotnet-sampled-thread-time,dotnet-common `. Use another profile only if you have a specific reason. CPU sampling is the best first pass for prompt-editor lag because it shows where active CPU time is spent.

### If `dotnet-trace` Is Missing

If you see:

```text
dotnet-trace: The term 'dotnet-trace' is not recognized
```

install it with:

```powershell
dotnet tool install -g dotnet-trace
```

### If You Need A Viewer But Not Speedscope

You can analyze `.nettrace` files directly in:

- PerfView
- Visual Studio profiler tooling

Use whatever tool your local environment already supports best. The key part is capturing a narrow prompt-editor scenario first.
