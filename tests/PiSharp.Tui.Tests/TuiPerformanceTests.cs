using System.Diagnostics;
using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Options;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core.Models;
using PiSharp.Tui.Interactive.Harness;
using PiSharp.Tui.Interactive;
using PiSharp.Tui.Interactive.Components;
using PiSharp.Tui.Interactive.Rendering;
using Terminal.Gui;
using Xunit;

namespace PiSharp.Tui.Tests;

[Collection(TuiIntegrationTestCollection.Name)]
public sealed class TuiPerformanceTests
{
    private const long StreamingAllocationBudgetBytes = 32L * 1024 * 1024;
    private const long LoadHarnessAllocationBudgetBytes = 256L * 1024 * 1024;
    private const long BatchedTranscriptUpdateAllocationBudgetBytes = 2L * 1024 * 1024;

    [Fact]
    public async Task RealHostPromptTypingBurstKeepsUiResponsive()
    {
        var startupMessages = Enumerable.Range(0, 200)
            .Select(index => $"startup message {index}")
            .ToArray();

        await using var running = await TuiIntegrationTestHost.StartAsync(startupMessages: startupMessages);

        var metrics = await TuiScenarioMetrics.MeasurePromptTypingBurstAsync(
            running,
            "typing burst through live host");

        Assert.Equal("typing burst through live host", metrics.FinalPromptText);
        Assert.True(metrics.UiResponsiveAfterBurst, metrics.ScreenText);
        Assert.True(metrics.Elapsed < TimeSpan.FromSeconds(2), $"Typing burst took {metrics.Elapsed}.\nScreen:\n{metrics.ScreenText}");
    }

    [Fact]
    public async Task RealHostPromptTypingBurstDoesNotExplodeTranscriptOrLayoutWork()
    {
        var startupMessages = Enumerable.Range(0, 200)
            .Select(index => $"startup message {index}")
            .ToArray();

        await using var running = await TuiIntegrationTestHost.StartAsync(startupMessages: startupMessages);

        var metrics = await TuiScenarioMetrics.MeasurePromptTypingBurstAsync(running, new string('a', 20));

        Assert.True(metrics.LayoutApplyCount <= 25, metrics.FormatFailure());
        Assert.True(metrics.TranscriptItemRenderCount <= 260, metrics.FormatFailure());
    }

    [Fact]
    public async Task RealHostMultilineTypingBurstTracksPromptHeightWithoutExcessiveLayoutWork()
    {
        await using var running = await TuiIntegrationTestHost.StartAsync();

        var metrics = await TuiScenarioMetrics.MeasurePromptKeyBurstAsync(
            running,
            [
                new Key((KeyCode)'o'),
                new Key((KeyCode)'n'),
                new Key((KeyCode)'e'),
                Key.Enter.WithShift,
                new Key((KeyCode)'t'),
                new Key((KeyCode)'w'),
                new Key((KeyCode)'o'),
                Key.Enter.WithShift,
                new Key((KeyCode)'t'),
                new Key((KeyCode)'h'),
                new Key((KeyCode)'r'),
                new Key((KeyCode)'e'),
                new Key((KeyCode)'e'),
                Key.Enter.WithShift,
                new Key((KeyCode)'f'),
                new Key((KeyCode)'o'),
                new Key((KeyCode)'u'),
                new Key((KeyCode)'r')
            ],
            requestRenderAfterEachKey: true);

        Assert.Equal(4, metrics.FinalPromptHeight);
        Assert.True(metrics.LayoutApplyCount <= 20, metrics.FormatFailure());
    }

    [Fact]
    public async Task RealHostCommandCompletionTypingBurstMeasuresCompletionChurn()
    {
        await using var running = await TuiIntegrationTestHost.StartAsync(
            completeCommand: text => ["/help", "/hello", "/health"]);

        var metrics = await TuiScenarioMetrics.MeasurePromptTypingBurstAsync(running, "/he");

        Assert.True(metrics.CompletionInvocationCount > 0, metrics.FormatFailure());
        Assert.True(metrics.CompletionInvocationCount <= 12, metrics.FormatFailure());
        Assert.NotEmpty(running.Context.Prompt.Completions);
    }

    [Fact]
    public async Task HarnessSubscriptionBatchesBurstEventsIntoOneStateUpdateAndRender()
    {
        var harness = TuiIntegrationTestHost.CreateHarness();
        var state = Empty();
        var setStateCalls = 0;
        var scheduledRenders = 0;
        using var subscription = new TuiHarnessSubscription(
            () => harness,
            () => state,
            next =>
            {
                state = next;
                setStateCalls++;
            },
            _ => scheduledRenders++,
            action => action(),
            resolveTool: null,
            loadSessionSnapshot: _ => Task.FromResult<TuiSessionSnapshot?>(null),
            applySessionSnapshot: (_, _) => { },
            eventBatchInterval: TuiTimingOptions.Default.HarnessEventBatchInterval);

        subscription.Bind();

        await harness.SetThinkingLevelAsync(ThinkingLevel.High);
        await WaitForConditionAsync(() => setStateCalls > 0, TimeSpan.FromSeconds(1));

        Assert.Equal(ThinkingLevel.High, state.ThinkingLevel);
        Assert.Equal(1, setStateCalls);
        Assert.Equal(1, scheduledRenders);
    }

    [Fact]
    public async Task TuiHarnessEventPumpBatchesBurstIntoSingleDispatch()
    {
        var batches = new List<IReadOnlyList<QueuedHarnessEvent>>();
        using var pump = new TuiHarnessEventPump(
            batch => batches.Add(batch.ToArray()),
            action => action(),
            TimeSpan.FromMilliseconds(10),
            capacity: 16,
            batchSize: 8);
        var first = ThinkingEvent(ThinkingLevel.Low);
        var second = ThinkingEvent(ThinkingLevel.High);

        Assert.Equal(TuiHarnessEventEnqueueResult.QueuedImmediately, pump.Enqueue(first, CancellationToken.None));
        Assert.Equal(TuiHarnessEventEnqueueResult.QueuedImmediately, pump.Enqueue(second, CancellationToken.None));

        await WaitForConditionAsync(() => batches.Count == 1, TimeSpan.FromSeconds(1));

        var batch = Assert.Single(batches);
        Assert.Equal([first, second], batch.Select(item => item.Event).ToArray());
    }

    [Fact]
    public async Task TuiHarnessEventPumpFlushesAtBatchSizeWithoutWaitingForInterval()
    {
        var batches = new List<IReadOnlyList<QueuedHarnessEvent>>();
        using var pump = new TuiHarnessEventPump(
            batch => batches.Add(batch.ToArray()),
            action => action(),
            TimeSpan.FromSeconds(30),
            capacity: 16,
            batchSize: 2);
        var first = ThinkingEvent(ThinkingLevel.Low);
        var second = ThinkingEvent(ThinkingLevel.High);

        Assert.Equal(TuiHarnessEventEnqueueResult.QueuedImmediately, pump.Enqueue(first, CancellationToken.None));
        Assert.Equal(TuiHarnessEventEnqueueResult.QueuedImmediately, pump.Enqueue(second, CancellationToken.None));

        await WaitForConditionAsync(() => batches.Count == 1, TimeSpan.FromSeconds(1));

        var batch = Assert.Single(batches);
        Assert.Equal([first, second], batch.Select(item => item.Event).ToArray());
    }

    [Fact]
    public async Task TuiHarnessEventPumpPreservesOrderThroughAsyncFallbackWrite()
    {
        var batches = new List<IReadOnlyList<QueuedHarnessEvent>>();
        using var firstDispatchStarted = new ManualResetEventSlim(false);
        using var releaseFirstDispatch = new ManualResetEventSlim(false);
        var dispatchCount = 0;
        using var pump = new TuiHarnessEventPump(
            batch => batches.Add(batch.ToArray()),
            action =>
            {
                if (Interlocked.Increment(ref dispatchCount) == 1)
                {
                    firstDispatchStarted.Set();
                    if (!releaseFirstDispatch.Wait(TimeSpan.FromSeconds(1))) return;
                }

                action();
            },
            TimeSpan.FromSeconds(30),
            capacity: 1,
            batchSize: 1);
        var first = ThinkingEvent(ThinkingLevel.Low);
        var second = ThinkingEvent(ThinkingLevel.Medium);
        var third = ThinkingEvent(ThinkingLevel.High);

        Assert.Equal(TuiHarnessEventEnqueueResult.QueuedImmediately, pump.Enqueue(first, CancellationToken.None));
        Assert.True(firstDispatchStarted.Wait(TimeSpan.FromSeconds(1)));
        Assert.Equal(TuiHarnessEventEnqueueResult.QueuedImmediately, pump.Enqueue(second, CancellationToken.None));
        Assert.Equal(TuiHarnessEventEnqueueResult.QueuedAsynchronously, pump.Enqueue(third, CancellationToken.None));

        releaseFirstDispatch.Set();
        await WaitForConditionAsync(() => batches.Count == 3, TimeSpan.FromSeconds(1));

        Assert.Equal([first, second, third], batches.SelectMany(batch => batch.Select(item => item.Event)).ToArray());
    }

    [Fact]
    public void TuiHarnessEventPumpEnqueueReturnsNotQueuedAfterDispose()
    {
        var pump = new TuiHarnessEventPump(
            _ => { },
            action => action(),
            TimeSpan.FromMilliseconds(10),
            capacity: 1,
            batchSize: 1);

        pump.Dispose();

        Assert.Equal(TuiHarnessEventEnqueueResult.NotQueued, pump.Enqueue(ThinkingEvent(ThinkingLevel.Low), CancellationToken.None));
    }

    [Fact]
    public async Task TuiHarnessEventPumpDisposeCompletesWithPendingAsyncFallbackWrite()
    {
        using var firstDispatchStarted = new ManualResetEventSlim(false);
        using var releaseFirstDispatch = new ManualResetEventSlim(false);
        var dispatchCount = 0;
        var pump = new TuiHarnessEventPump(
            _ => { },
            action =>
            {
                if (Interlocked.Increment(ref dispatchCount) == 1)
                {
                    firstDispatchStarted.Set();
                    releaseFirstDispatch.Wait(TimeSpan.FromSeconds(1));
                }

                action();
            },
            TimeSpan.FromSeconds(30),
            capacity: 1,
            batchSize: 1);

        Assert.Equal(TuiHarnessEventEnqueueResult.QueuedImmediately, pump.Enqueue(ThinkingEvent(ThinkingLevel.Low), CancellationToken.None));
        Assert.True(firstDispatchStarted.Wait(TimeSpan.FromSeconds(1)));
        Assert.Equal(TuiHarnessEventEnqueueResult.QueuedImmediately, pump.Enqueue(ThinkingEvent(ThinkingLevel.Medium), CancellationToken.None));
        Assert.Equal(TuiHarnessEventEnqueueResult.QueuedAsynchronously, pump.Enqueue(ThinkingEvent(ThinkingLevel.High), CancellationToken.None));

        var disposeTask = Task.Run(pump.Dispose);
        releaseFirstDispatch.Set();

        await WaitForConditionAsync(() => disposeTask.IsCompleted, TimeSpan.FromSeconds(1));
        await disposeTask;
    }

    [Fact]
    public void ChatViewSecondRenderWithSameStateDoesNotRerenderTranscriptItems()
    {
        var renderCalls = 0;
        var chat = CountingChatView(_ => renderCalls++);
        var state = LargeTranscriptState(1_000);

        chat.Render(state);
        chat.Render(state);

        Assert.Equal(1_000, renderCalls);
    }

    [Fact]
    public void ChatViewPromptOnlyStateChangeDoesNotRerenderTranscriptItems()
    {
        var renderCalls = 0;
        var chat = CountingChatView(_ => renderCalls++);
        var state = LargeTranscriptState(1_000);

        chat.Render(state);
        chat.Render(state.SetEditorText("typed"));

        Assert.Equal(1_000, renderCalls);
    }

    [Fact]
    public void ChatViewPromptOnlyStateChangeReusesRenderedRows()
    {
        var chat = CountingChatView(_ => { });
        var state = LargeTranscriptState(1_000);

        chat.Render(state);
        var rows = chat.Rows;
        chat.Render(state.SetEditorText("typed"));

        Assert.Same(rows, chat.Rows);
    }

    [Fact]
    public void ChatViewDoesNotMirrorRowsIntoTextBuffer()
    {
        var chat = CountingChatView(_ => { });

        chat.Render(LargeTranscriptState(1_000));

        Assert.Equal(string.Empty, chat.Text?.ToString() ?? string.Empty);
        Assert.True(chat.Rows.Count > 1_000);
    }

    [Fact]
    public void ChatViewWidthChangeInvalidatesRowCache()
    {
        var renderCalls = 0;
        var chat = CountingChatView(_ => renderCalls++);
        chat.Width = 100;
        var state = LargeTranscriptState(100);

        chat.Render(state);
        chat.Width = 80;
        chat.Render(state);

        Assert.Equal(200, renderCalls);
    }

    [Fact]
    public void ChatViewToolOutputAndThinkingTogglesInvalidateAffectedRows()
    {
        var renderCalls = 0;
        var chat = CountingChatView(_ => renderCalls++);
        var state = Empty() with
        {
            Transcript =
            [
                new TuiTranscriptItem("assistant", string.Empty, Content: [new ThinkingContent("secret")]),
                new TuiTranscriptItem("tool", "details", ToolName: "read", ToolCallId: "tool-1")
            ]
        };

        chat.Render(state);
        chat.Render(state.ToggleThinking());
        chat.Render(state.ToggleToolOutput());

        Assert.Equal(6, renderCalls);
    }

    [Fact]
    public void StreamingAssistantUpdateOnlyRendersActiveAssistantRow()
    {
        var rendered = new Dictionary<string, int>(StringComparer.Ordinal);
        var chat = CountingChatView(item =>
        {
            var key = item.EntryId ?? item.Text;
            rendered[key] = rendered.GetValueOrDefault(key) + 1;
        });
        var transcript = Enumerable.Range(0, 1_000)
            .Select(index => new TuiTranscriptItem("assistant", $"stable {index}", EntryId: $"stable-{index}"))
            .Append(new TuiTranscriptItem("assistant", "stream 0", true, EntryId: "streaming"))
            .ToArray();
        var state = Empty() with { Transcript = transcript };
        chat.Render(state);

        for (var index = 1; index <= 100; index++)
        {
            state = state with
            {
                Transcript = transcript[..^1].Append(new TuiTranscriptItem("assistant", $"stream {index}", true, EntryId: "streaming")).ToArray()
            };
            chat.Render(state);
        }

        Assert.All(rendered.Where(pair => pair.Key.StartsWith("stable-", StringComparison.Ordinal)), pair => Assert.Equal(1, pair.Value));
        Assert.Equal(101, rendered["streaming"]);
    }

    [Fact]
    public void ChatViewStreamingUpdateDoesNotRebuildAllRowGroups()
    {
        var counters = new TuiProfilingCounters();
        var chat = CountingChatView(_ => { });
        chat.ProfilingCounters = counters;
        var transcript = Enumerable.Range(0, 1_000)
            .Select(index => new TuiTranscriptItem("assistant", $"stable {index}", EntryId: $"stable-{index}"))
            .Append(new TuiTranscriptItem("assistant", "stream 0", true, EntryId: "streaming"))
            .ToArray();
        var state = Empty() with { Transcript = transcript };
        chat.Render(state);
        var plannedAfterInitialRender = counters.GetCount(TuiProfilingCounterNames.ChatRowGroupPlan);

        state = state with
        {
            Transcript = transcript[..^1].Append(new TuiTranscriptItem("assistant", "stream 1", true, EntryId: "streaming")).ToArray()
        };
        chat.Render(state);

        var plannedForStreamingUpdate = counters.GetCount(TuiProfilingCounterNames.ChatRowGroupPlan) - plannedAfterInitialRender;
        Assert.True(plannedForStreamingUpdate <= 1, $"Expected only active row group to be planned; planned {plannedForStreamingUpdate} groups.");
    }

    [Fact]
    public void ChatTranscriptRowPlannerReusesStableRowGroupsWhenOneItemChanges()
    {
        var counters = new TuiProfilingCounters();
        var planner = new ChatTranscriptRowPlanner();
        var cache = CountingRowCache(_ => { });
        var initial = Empty() with
        {
            Transcript =
            [
                new TuiTranscriptItem("user", "hello", EntryId: "user-1"),
                new TuiTranscriptItem("assistant", "stable", EntryId: "assistant-1"),
                new TuiTranscriptItem("assistant", "stream 0", true, EntryId: "streaming")
            ]
        };
        var updated = initial with
        {
            Transcript =
            [
                initial.Transcript[0],
                initial.Transcript[1],
                new TuiTranscriptItem("assistant", "stream 1", true, EntryId: "streaming")
            ]
        };

        planner.BuildRows(initial, 100, cache, counters);
        var plannedAfterInitial = counters.GetCount(TuiProfilingCounterNames.ChatRowGroupPlan);
        planner.BuildRows(updated, 100, cache, counters);

        Assert.Equal(1, counters.GetCount(TuiProfilingCounterNames.ChatRowGroupPlan) - plannedAfterInitial);
    }

    [Fact]
    public void ChatTranscriptRowPlannerInvalidatesAllRowsWhenWidthChanges()
    {
        var counters = new TuiProfilingCounters();
        var planner = new ChatTranscriptRowPlanner();
        var cache = CountingRowCache(_ => { });
        var state = Empty() with
        {
            Transcript =
            [
                new TuiTranscriptItem("user", "hello", EntryId: "user-1"),
                new TuiTranscriptItem("assistant", "stable", EntryId: "assistant-1"),
                new TuiTranscriptItem("assistant", "stream", true, EntryId: "streaming")
            ]
        };

        planner.BuildRows(state, 100, cache, counters);
        var plannedAfterInitial = counters.GetCount(TuiProfilingCounterNames.ChatRowGroupPlan);
        planner.BuildRows(state, 80, cache, counters);

        Assert.Equal(state.Transcript.Count, counters.GetCount(TuiProfilingCounterNames.ChatRowGroupPlan) - plannedAfterInitial);
    }

    [Fact]
    public void StreamingBurstAllocationsStayBelowRegressionBudget()
    {
        var chat = CountingChatView(_ => { });
        var transcript = Enumerable.Range(0, 1_000)
            .Select(index => new TuiTranscriptItem("assistant", $"stable {index}", EntryId: $"stable-{index}"))
            .Append(new TuiTranscriptItem("assistant", "stream 0", true, EntryId: "streaming"))
            .ToArray();
        var state = Empty() with { Transcript = transcript };
        chat.Render(state);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 1; index <= 100; index++)
        {
            state = state with
            {
                Transcript = transcript[..^1].Append(new TuiTranscriptItem("assistant", $"stream {index}", true, EntryId: "streaming")).ToArray()
            };
            chat.Render(state);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < StreamingAllocationBudgetBytes, $"Streaming burst allocated {allocated:n0} bytes; budget is {StreamingAllocationBudgetBytes:n0} bytes.");
    }

    [Fact]
    public void BatchedToolUpdatesAvoidRepeatedTranscriptCopies()
    {
        using var args = JsonDocument.Parse("{}");
        var transcript = Enumerable.Range(0, 2_000)
            .Select(index => new TuiTranscriptItem(index % 2 == 0 ? "user" : "assistant", $"message {index}", EntryId: $"entry-{index}"))
            .Append(new TuiTranscriptItem("tool", "initial", true, "read", ToolCallId: "tool-1"))
            .ToArray();
        var state = Empty() with { Transcript = transcript };
        var events = Enumerable.Range(0, 500)
            .Select(index => new AgentHarnessEvent.Core(new AgentEvent.ToolExecutionUpdate("tool-1", "read", args.RootElement.Clone(), $"partial {index}")))
            .Cast<AgentHarnessEvent>()
            .ToArray();

        var before = GC.GetAllocatedBytesForCurrentThread();
        state = state.ReduceBatch(events);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        var tool = Assert.Single(state.Transcript, item => item.ToolCallId == "tool-1");
        Assert.Equal("partial 499", tool.Text);
        Assert.True(allocated < BatchedTranscriptUpdateAllocationBudgetBytes, $"Batched tool updates allocated {allocated:n0} bytes; budget is {BatchedTranscriptUpdateAllocationBudgetBytes:n0} bytes.");
    }

    [Fact]
    public void OptionalTuiLoadHarnessCapturesLargeSessionMetrics()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("PISHARP_RUN_TUI_LOAD_TESTS"), "1", StringComparison.Ordinal)) return;

        var renderCalls = 0;
        var schedulerRequests = 0;
        var schedulerExecuted = 0;
        var scheduledFrames = new List<Action>();
        var scheduler = new TuiRenderScheduler(dispatch: action => action(), scheduleFrame: (action, _) => scheduledFrames.Add(action));
        var branchResolverCalls = 0;
        var footerProvider = new TuiFooterSnapshotProvider(resolveGitBranch: _ =>
        {
            branchResolverCalls++;
            return "main";
        }, clock: () => DateTimeOffset.UnixEpoch, gitBranchCacheDuration: TimeSpan.FromMinutes(1));
        var chat = CountingChatView(_ => renderCalls++);
        var state = LargeTranscriptState(2_500);

        var coldRender = Stopwatch.StartNew();
        chat.Render(state);
        coldRender.Stop();

        var promptAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var promptOnly = Stopwatch.StartNew();
        for (var index = 0; index < 1_000; index++)
        {
            schedulerRequests++;
            scheduler.RequestRender(() => schedulerExecuted++);
            chat.Render(state.SetEditorText($"typed {index}"));
            footerProvider.CreateSnapshot(state, "repo");
        }
        promptOnly.Stop();
        var promptAllocated = GC.GetAllocatedBytesForCurrentThread() - promptAllocatedBefore;

        var transcript = state.Transcript
            .Append(new TuiTranscriptItem("assistant", "stream 0", true, EntryId: "streaming"))
            .ToArray();
        var streamingState = state with { Transcript = transcript };
        var streamingAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var streaming = Stopwatch.StartNew();
        for (var index = 1; index <= 1_000; index++)
        {
            schedulerRequests++;
            scheduler.RequestRender(() => schedulerExecuted++);
            streamingState = streamingState with
            {
                Transcript = transcript[..^1].Append(new TuiTranscriptItem("assistant", $"stream {index}", true, EntryId: "streaming")).ToArray()
            };
            chat.Render(streamingState);
        }
        streaming.Stop();
        var streamingAllocated = GC.GetAllocatedBytesForCurrentThread() - streamingAllocatedBefore;

        var frame = Assert.Single(scheduledFrames);
        frame();

        var stableRenderCalls = renderCalls - 2_500;
        Assert.True(stableRenderCalls <= 1_001, $"Expected cached stable rows; observed {stableRenderCalls} extra render calls.");
        Assert.Equal(2_000, schedulerRequests);
        Assert.Equal(1, schedulerExecuted);
        Assert.Equal(1, branchResolverCalls);
        Assert.True(promptAllocated < LoadHarnessAllocationBudgetBytes, $"Prompt-only path allocated {promptAllocated:n0} bytes; budget is {LoadHarnessAllocationBudgetBytes:n0} bytes.");
        Assert.True(streamingAllocated < LoadHarnessAllocationBudgetBytes, $"Streaming path allocated {streamingAllocated:n0} bytes; budget is {LoadHarnessAllocationBudgetBytes:n0} bytes.");
        Console.WriteLine($"cold={coldRender.ElapsedMilliseconds}ms promptOnly={promptOnly.ElapsedMilliseconds}ms streaming={streaming.ElapsedMilliseconds}ms renderCalls={renderCalls} rendersRequested={schedulerRequests} rendersExecuted={schedulerExecuted} promptAllocated={promptAllocated} streamingAllocated={streamingAllocated} gitBranchCalls={branchResolverCalls}");
    }

    [Fact]
    public async Task OptionalRealHostTuiProfilingProbeCapturesNamedCounters()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("PISHARP_RUN_TUI_PROFILING_TESTS"), "1", StringComparison.Ordinal)) return;

        await using var running = await TuiIntegrationTestHost.StartAsync();

        var providerBefore = running.ProfilingCounters.GetCount(TuiProfilingCounterNames.FileReferenceCompletion);
        var fileSystemBefore = running.ProfilingCounters.GetCount(TuiProfilingCounterNames.FileReferenceFileSystem);
        var gitBefore = running.ProfilingCounters.GetCount(TuiProfilingCounterNames.FileReferenceGitVisibility);

        var metrics = await TuiScenarioMetrics.MeasurePromptTypingBurstAsync(running, "@src/");

        var providerDelta = running.ProfilingCounters.GetCount(TuiProfilingCounterNames.FileReferenceCompletion) - providerBefore;
        var fileSystemDelta = running.ProfilingCounters.GetCount(TuiProfilingCounterNames.FileReferenceFileSystem) - fileSystemBefore;
        var gitDelta = running.ProfilingCounters.GetCount(TuiProfilingCounterNames.FileReferenceGitVisibility) - gitBefore;

        Assert.True(metrics.CompletionInvocationCount > 0, metrics.FormatFailure());
        Assert.True(providerDelta > 0, metrics.FormatFailure());
        Assert.True(fileSystemDelta > 0, metrics.FormatFailure());

        Console.WriteLine($"elapsed={metrics.Elapsed.TotalMilliseconds:n0}ms completions={metrics.CompletionInvocationCount} renderCycles={metrics.RenderCycleCount} layoutApplies={metrics.LayoutApplyCount} transcriptItemRenders={metrics.TranscriptItemRenderCount} fileReferenceProvider={providerDelta} fileSystemCalls={fileSystemDelta} gitVisibilityCalls={gitDelta}");
    }

    private static ChatView CountingChatView(Action<TuiTranscriptItem> onRender)
        => new()
        {
            Width = 120,
            TranscriptItemRenderer = (item, _, width) =>
            {
                onRender(item);
                return [new TuiChatRow(item.Text, TuiChatRowKind.Normal).PadTo(width)];
            }
        };

    private static ChatRowCache CountingRowCache(Action<TuiTranscriptItem> onRender)
        => new((item, _, width) =>
        {
            onRender(item);
            return [new TuiChatRow(item.Text, TuiChatRowKind.Normal).PadTo(width)];
        });

    private static TuiRenderState LargeTranscriptState(int count)
        => Empty() with
        {
            Transcript = Enumerable.Range(0, count)
                .Select(index => new TuiTranscriptItem(index % 2 == 0 ? "user" : "assistant", $"message {index}", EntryId: $"entry-{index}"))
                .ToArray()
        };

    private static TuiRenderState Empty()
        => TuiRenderState.Empty("sid", "session.jsonl", new ModelDescriptor("test", "model", "test", ContextWindow: 100), ThinkingLevel.Off, null);

    private static AgentHarnessEvent ThinkingEvent(ThinkingLevel level)
        => new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.ThinkingLevelSelect(level, ThinkingLevel.Off));

    private static async Task WaitForConditionAsync(Func<bool> condition, TimeSpan timeout)
    {
        var stopAt = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < stopAt)
        {
            if (condition()) return;
            await Task.Delay(25);
        }

        Assert.True(condition(), "Expected condition to become true before timeout.");
    }
}
