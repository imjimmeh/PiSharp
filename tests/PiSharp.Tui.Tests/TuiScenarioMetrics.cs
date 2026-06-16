using System.Diagnostics;
using PiSharp.Tui.Interactive;
using Terminal.Gui;

namespace PiSharp.Tui.Tests;

internal sealed record TuiScenarioMeasurement(
    TimeSpan Elapsed,
    string FinalPromptText,
    bool UiResponsiveAfterBurst,
    string ScreenText,
    int KeyCount,
    int FinalPromptHeight,
    long RenderCycleCount,
    long LayoutApplyCount,
    long TranscriptItemRenderCount,
    long CompletionInvocationCount)
{
    public string FormatFailure()
        => $"elapsed={Elapsed} keys={KeyCount} promptHeight={FinalPromptHeight} renderCycles={RenderCycleCount} layoutApplies={LayoutApplyCount} transcriptItemRenders={TranscriptItemRenderCount} completions={CompletionInvocationCount}\nprompt=\"{FinalPromptText}\"\nscreen:\n{ScreenText}";
}

internal static class TuiScenarioMetrics
{
    private static readonly TimeSpan RenderFlushTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CounterStabilityPollInterval = TimeSpan.FromMilliseconds(20);
    private const int RequiredStableCounterPolls = 2;

    public static async Task<TuiScenarioMeasurement> MeasurePromptTypingBurstAsync(
        RunningTuiHost running,
        string text,
        Func<RunningTuiHost, Task<bool>>? waitForIdle = null,
        bool requestRenderAfterEachKey = false,
        CancellationToken cancellationToken = default)
        => await MeasurePromptKeyBurstAsync(
            running,
            text.Select(ToPromptKey).ToArray(),
            waitForIdle,
            requestRenderAfterEachKey,
            cancellationToken);

    public static async Task<TuiScenarioMeasurement> MeasurePromptKeyBurstAsync(
        RunningTuiHost running,
        IReadOnlyList<Key> keys,
        Func<RunningTuiHost, Task<bool>>? waitForIdle = null,
        bool requestRenderAfterEachKey = false,
        CancellationToken cancellationToken = default)
    {
        waitForIdle ??= host => host.TryUiThreadActionAsync(TimeSpan.FromSeconds(1));

        await WaitForRenderQuiescenceAsync(running, cancellationToken, requestRender: true);

        var renderCyclesBefore = running.ProfilingCounters.GetCount(TuiProfilingCounterNames.RenderCycle);
        var layoutAppliesBefore = running.ProfilingCounters.GetCount(TuiProfilingCounterNames.LayoutApply);
        var transcriptItemRendersBefore = running.ProfilingCounters.GetCount(TuiProfilingCounterNames.TranscriptItemRender);
        var completionInvocationsBefore = running.ProfilingCounters.GetCount(TuiProfilingCounterNames.CompletionInvocation);

        var stopwatch = Stopwatch.StartNew();
        foreach (var key in keys)
        {
            await running.SendPromptKeyAsync(key, cancellationToken: cancellationToken);
            if (requestRenderAfterEachKey) running.Context.RequestRender();
        }

        await WaitForRenderQuiescenceAsync(
            running,
            cancellationToken,
            renderCycleFloor: requestRenderAfterEachKey ? renderCyclesBefore : null);
        var uiResponsiveAfterBurst = await waitForIdle(running);
        stopwatch.Stop();

        return new TuiScenarioMeasurement(
            stopwatch.Elapsed,
            running.Context.Prompt.PromptText,
            uiResponsiveAfterBurst,
            running.ScreenText,
            keys.Count,
            running.Context.Prompt.Frame.Height,
            running.ProfilingCounters.GetCount(TuiProfilingCounterNames.RenderCycle) - renderCyclesBefore,
            running.ProfilingCounters.GetCount(TuiProfilingCounterNames.LayoutApply) - layoutAppliesBefore,
            running.ProfilingCounters.GetCount(TuiProfilingCounterNames.TranscriptItemRender) - transcriptItemRendersBefore,
            running.ProfilingCounters.GetCount(TuiProfilingCounterNames.CompletionInvocation) - completionInvocationsBefore);
    }

    private static async Task WaitForRenderQuiescenceAsync(
        RunningTuiHost running,
        CancellationToken cancellationToken,
        long? renderCycleFloor = null,
        bool requestRender = false)
    {
        if (requestRender)
        {
            renderCycleFloor = running.ProfilingCounters.GetCount(TuiProfilingCounterNames.RenderCycle);
            running.Context.RequestRender();
        }

        if (renderCycleFloor.HasValue)
        {
            await running.WaitForAsync(
                () => running.ProfilingCounters.GetCount(TuiProfilingCounterNames.RenderCycle) > renderCycleFloor.Value,
                RenderFlushTimeout,
                cancellationToken);
        }

        var lastSnapshot = CaptureCounterSnapshot(running);
        var stablePolls = 0;
        var deadline = DateTime.UtcNow + RenderFlushTimeout;

        while (stablePolls < RequiredStableCounterPolls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"Profiling counters did not stabilize within {RenderFlushTimeout}.\nScreen:\n{running.ScreenText}");

            await Task.Delay(CounterStabilityPollInterval, cancellationToken);
            var uiResponsive = await running.TryUiThreadActionAsync(TimeSpan.FromSeconds(1));
            var currentSnapshot = CaptureCounterSnapshot(running);
            if (uiResponsive && currentSnapshot == lastSnapshot)
            {
                stablePolls++;
                continue;
            }

            stablePolls = 0;
            lastSnapshot = currentSnapshot;
        }
    }

    private static (long RenderCycles, long LayoutApplies, long TranscriptItemRenders, long CompletionInvocations) CaptureCounterSnapshot(RunningTuiHost running)
        => (
            running.ProfilingCounters.GetCount(TuiProfilingCounterNames.RenderCycle),
            running.ProfilingCounters.GetCount(TuiProfilingCounterNames.LayoutApply),
            running.ProfilingCounters.GetCount(TuiProfilingCounterNames.TranscriptItemRender),
            running.ProfilingCounters.GetCount(TuiProfilingCounterNames.CompletionInvocation));

    private static Key ToPromptKey(char ch)
        => ch switch
        {
            '\n' => new Key((KeyCode)'\n'),
            '\r' => new Key((KeyCode)'\n'),
            _ => new Key((KeyCode)ch)
        };
}
